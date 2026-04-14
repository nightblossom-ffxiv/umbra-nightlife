using System;
using System.Collections.Generic;
using UmbraNightlife.Models;

namespace UmbraNightlife.Services;

/// <summary>
/// Decides whether a venue is open right now and when it next opens.
///
/// FFXIVVenues already does the hard work for us: every <see cref="FfxivScheduleDto"/>
/// carries a <c>resolution</c> with the absolute UTC start/end of the next (or current)
/// occurrence, with biweekly / monthly intervals already applied. We trust that.
///
/// If <c>resolution</c> is missing (older or partial records), we fall back to walking
/// the weekly pattern from <c>utc</c>.
/// </summary>
public static class ScheduleCalculator
{
    public readonly record struct State(bool IsOpen, DateTime? CloseAtUtc, DateTime? NextOpenAtUtc);

    public static State Compute(IEnumerable<FfxivScheduleDto>? schedule, DateTime nowUtc)
    {
        DateTime? latestCloseAt = null;
        DateTime? earliestNextOpenAt = null;

        if (schedule is null)
            return new State(false, null, null);

        foreach (var entry in schedule)
        {
            // Preferred: trust the source's pre-resolved occurrence.
            if (entry.Resolution is { Start: { } resStart, End: { } resEnd })
            {
                var startUtc = resStart.UtcDateTime;
                var endUtc = resEnd.UtcDateTime;
                if (endUtc <= startUtc) endUtc = endUtc.AddDays(1); // defence

                if (startUtc <= nowUtc && nowUtc < endUtc)
                {
                    if (latestCloseAt is null || endUtc > latestCloseAt) latestCloseAt = endUtc;
                }
                else if (startUtc > nowUtc)
                {
                    if (earliestNextOpenAt is null || startUtc < earliestNextOpenAt)
                        earliestNextOpenAt = startUtc;
                }
                continue;
            }

            // Fallback: weekly pattern from the utc block (no biweekly-awareness here).
            ApplyWeeklyFallback(entry.Utc, nowUtc, ref latestCloseAt, ref earliestNextOpenAt);
        }

        return new State(latestCloseAt is not null, latestCloseAt, earliestNextOpenAt);
    }

    private static void ApplyWeeklyFallback(
        FfxivUtcBlockDto? utc,
        DateTime nowUtc,
        ref DateTime? latestCloseAt,
        ref DateTime? earliestNextOpenAt)
    {
        if (utc is null || utc.Start is null || utc.End is null) return;
        if (utc.Day is < 0 or > 6) return;
        if (!IsValidHm(utc.Start.Hour, utc.Start.Minute)) return;
        if (!IsValidHm(utc.End.Hour, utc.End.Minute)) return;

        var startTime = new TimeSpan(utc.Start.Hour, utc.Start.Minute, 0);
        var endTime = new TimeSpan(utc.End.Hour, utc.End.Minute, 0);
        var endNextDay = utc.End.NextDay;
        if (!endNextDay && endTime <= startTime) return;

        var targetDotNetDow = FfxivDayToDotNet(utc.Day);
        for (var weekOffset = -1; weekOffset <= 1; weekOffset++)
        {
            var startDate = NearestDayOfWeek(nowUtc.Date, targetDotNetDow, weekOffset);
            var startAt = startDate.Add(startTime);
            var endAt = (endNextDay ? startDate.AddDays(1) : startDate).Add(endTime);
            if (endAt <= startAt) endAt = endAt.AddDays(1);

            if (startAt <= nowUtc && nowUtc < endAt)
            {
                if (latestCloseAt is null || endAt > latestCloseAt) latestCloseAt = endAt;
            }
            else if (startAt > nowUtc)
            {
                if (earliestNextOpenAt is null || startAt < earliestNextOpenAt)
                    earliestNextOpenAt = startAt;
            }
        }
    }

    private static DateTime NearestDayOfWeek(DateTime utcDate, int targetDow, int weekOffset)
    {
        var today = (int)utcDate.DayOfWeek; // 0=Sun..6=Sat
        var delta = targetDow - today + 7 * weekOffset;
        return utcDate.AddDays(delta);
    }

    private static bool IsValidHm(int hour, int minute)
        => hour is >= 0 and <= 23 && minute is >= 0 and <= 59;

    /// <summary>
    /// FFXIVVenues uses ISO day numbering (0=Mon..6=Sun); .NET's <see cref="DayOfWeek"/>
    /// uses 0=Sun..6=Sat. This is the conversion between them.
    /// </summary>
    internal static int FfxivDayToDotNet(int ffxivDay) => (ffxivDay + 1) % 7;
}
