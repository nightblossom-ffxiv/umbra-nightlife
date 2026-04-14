using System;
using System.Collections.Generic;
using UmbraNightlife.Models;

namespace UmbraNightlife.Services;

/// <summary>
/// Given a venue's UTC schedule blocks, reports whether it is open now and when
/// it opens next. Handles slots that cross UTC midnight and overlapping slots.
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
            var utc = entry.Utc;
            if (utc is null || utc.Start is null || utc.End is null) continue;
            if (utc.Day is < 0 or > 6) continue;
            if (!IsValidHm(utc.Start.Hour, utc.Start.Minute)) continue;
            if (!IsValidHm(utc.End.Hour, utc.End.Minute)) continue;

            var startTime = new TimeSpan(utc.Start.Hour, utc.Start.Minute, 0);
            var endTime = new TimeSpan(utc.End.Hour, utc.End.Minute, 0);
            var endNextDay = utc.End.NextDay;

            // Drop degenerate (end ≤ start and not crossing midnight).
            if (!endNextDay && endTime <= startTime) continue;

            // FFXIVVenues encodes days as 0=Mon..6=Sun (ISO), but .NET's DayOfWeek
            // is 0=Sun..6=Sat. Convert once here so NearestDayOfWeek stays simple.
            var targetDotNetDow = FfxivDayToDotNet(utc.Day);

            // Check three adjacent weeks so we catch in-progress + next-future windows.
            for (var weekOffset = -1; weekOffset <= 1; weekOffset++)
            {
                var startDate = NearestDayOfWeek(nowUtc.Date, targetDotNetDow, weekOffset);
                var startAt = startDate.Add(startTime);
                var endAt = (endNextDay ? startDate.AddDays(1) : startDate).Add(endTime);
                if (endAt <= startAt) endAt = endAt.AddDays(1); // defence-in-depth

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

        return new State(latestCloseAt is not null, latestCloseAt, earliestNextOpenAt);
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
