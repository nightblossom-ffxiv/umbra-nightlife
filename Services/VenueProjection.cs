using System;
using System.Collections.Generic;
using System.Linq;
using UmbraNightlife.Models;

namespace UmbraNightlife.Services;

/// <summary>Pure mapping from source DTO → render-ready view.</summary>
public static class VenueProjection
{
    public static VenueView? Project(FfxivVenueDto dto, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(dto.Id) || !dto.Approved) return null;

        var loc = dto.Location ?? new FfxivLocationDto();
        var (isOpen, closeAt, nextOpen) = ScheduleCalculator.Compute(dto.Schedule, nowUtc);

        return new VenueView(
            Id: dto.Id!,
            Name: (dto.Name ?? "").Trim(),
            DataCenter: loc.DataCenter ?? "",
            World: loc.World ?? "",
            District: loc.District,
            Ward: loc.Ward,
            Plot: loc.Plot is > 0 ? loc.Plot : null,
            Room: loc.Room is > 0 ? loc.Room : null,
            Subdivision: loc.Subdivision,
            Tags: dto.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>(),
            Sfw: dto.Sfw,
            Hiring: dto.Hiring,
            Website: dto.Website,
            Discord: dto.Discord,
            IsOpenNow: isOpen,
            CurrentCloseAtUtc: closeAt,
            NextOpenAtUtc: nextOpen);
    }
}
