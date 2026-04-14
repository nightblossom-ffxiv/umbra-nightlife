using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UmbraNightlife.Models;

// DTOs that mirror the shape of https://api.ffxivvenues.com/venue.
// Only the fields we actually render are declared; unknown fields are ignored by System.Text.Json.

public sealed class FfxivVenueDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("bannerUri")] public string? BannerUri { get; set; }
    [JsonPropertyName("description")] public List<string>? Description { get; set; }
    [JsonPropertyName("location")] public FfxivLocationDto? Location { get; set; }
    [JsonPropertyName("website")] public string? Website { get; set; }
    [JsonPropertyName("discord")] public string? Discord { get; set; }
    [JsonPropertyName("hiring")] public bool Hiring { get; set; }
    [JsonPropertyName("sfw")] public bool Sfw { get; set; } = true;
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("schedule")] public List<FfxivScheduleDto>? Schedule { get; set; }
    [JsonPropertyName("approved")] public bool Approved { get; set; }
}

public sealed class FfxivLocationDto
{
    [JsonPropertyName("dataCenter")] public string? DataCenter { get; set; }
    [JsonPropertyName("world")] public string? World { get; set; }
    [JsonPropertyName("district")] public string? District { get; set; }
    [JsonPropertyName("ward")] public int? Ward { get; set; }
    [JsonPropertyName("plot")] public int? Plot { get; set; }
    [JsonPropertyName("apartment")] public int? Apartment { get; set; }
    [JsonPropertyName("room")] public int? Room { get; set; }
    [JsonPropertyName("subdivision")] public bool Subdivision { get; set; }
}

public sealed class FfxivScheduleDto
{
    [JsonPropertyName("day")] public int Day { get; set; }
    [JsonPropertyName("utc")] public FfxivUtcBlockDto? Utc { get; set; }

    /// <summary>
    /// Pre-resolved next occurrence — already accounts for biweekly / monthly intervals
    /// and the schedule's <c>commencing</c> date. Trust this over computing the weekly
    /// pattern ourselves; otherwise biweekly venues like Bliss show as open every week.
    /// </summary>
    [JsonPropertyName("resolution")] public FfxivResolutionDto? Resolution { get; set; }
}

public sealed class FfxivResolutionDto
{
    [JsonPropertyName("start")] public System.DateTimeOffset? Start { get; set; }
    [JsonPropertyName("end")] public System.DateTimeOffset? End { get; set; }
}

public sealed class FfxivUtcBlockDto
{
    [JsonPropertyName("day")] public int Day { get; set; }
    [JsonPropertyName("start")] public FfxivTimeDto? Start { get; set; }
    [JsonPropertyName("end")] public FfxivTimeDto? End { get; set; }
}

public sealed class FfxivTimeDto
{
    [JsonPropertyName("hour")] public int Hour { get; set; }
    [JsonPropertyName("minute")] public int Minute { get; set; }
    [JsonPropertyName("nextDay")] public bool NextDay { get; set; }
}
