using System;
using System.Collections.Generic;

namespace UmbraNightlife.Models;

/// <summary>
/// Render-ready view of a venue. Built by <see cref="Services.VenueProjection"/>
/// from a <see cref="FfxivVenueDto"/> plus the current time.
/// </summary>
public sealed record VenueView(
    string Id,
    string Name,
    string DataCenter,
    string World,
    string? District,
    int? Ward,
    int? Plot,
    int? Room,
    bool Subdivision,
    IReadOnlyList<string> Tags,
    bool Sfw,
    bool Hiring,
    string? Website,
    string? Discord,
    bool IsOpenNow,
    DateTime? CurrentCloseAtUtc,
    DateTime? NextOpenAtUtc)
{
    /// <summary>"Mist, Ward 14, Plot 22" style address.</summary>
    public string FriendlyAddress
    {
        get
        {
            var parts = new List<string>(4);
            if (!string.IsNullOrWhiteSpace(District)) parts.Add(District!);
            if (Ward is > 0) parts.Add($"Ward {Ward}");
            if (Plot is > 0) parts.Add($"Plot {Plot}");
            if (Room is > 0) parts.Add($"Room {Room}");
            if (Subdivision) parts.Add("Subdivision");
            return string.Join(", ", parts);
        }
    }

    /// <summary>Address string passed to Lifestream's <c>/li</c> command.</summary>
    public string LifestreamAddress
    {
        get
        {
            var dc = string.IsNullOrWhiteSpace(DataCenter) ? World : $"{World}";
            var parts = new List<string> { dc };
            if (!string.IsNullOrWhiteSpace(District)) parts.Add(District!);
            if (Ward is > 0) parts.Add($"W{Ward}");
            if (Plot is > 0) parts.Add($"P{Plot}");
            if (Room is > 0) parts.Add($"R{Room}");
            if (Subdivision) parts.Add("subdivision");
            return string.Join(" ", parts);
        }
    }
}
