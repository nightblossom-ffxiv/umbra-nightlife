using System;
using Dalamud.Game.Config;
using Dalamud.Plugin.Services;

namespace UmbraNightlife.Services;

/// <summary>
/// Renders a UTC timestamp the way the player has their in-game HUD clock set.
/// The game cycles between Eorzea / Local / Server when clicking the clock.
///
/// We honour that choice:
/// <list type="bullet">
///   <item>Server Time → UTC as-is (matches what venues publish).</item>
///   <item>Anything else → OS local time.</item>
/// </list>
/// Eorzea time is a fantasy clock (×175 speed) and makes no sense for real-world
/// schedules, so we treat it as "local" rather than convert.
/// </summary>
public static class TimeDisplay
{
    /// <summary>Game's internal enum value for Server Time.</summary>
    private const uint ServerTimeMode = 2;

    /// <summary>Returns "HH:mm ST" or "HH:mm LT" for the given UTC instant.</summary>
    public static string Format(DateTime utc, IGameConfig? gameConfig)
    {
        var (text, suffix) = Convert(utc, gameConfig);
        return $"{text:HH\\:mm} {suffix}";
    }

    /// <summary>"ddd HH:mm ST" — useful for times more than a day out.</summary>
    public static string FormatWithDay(DateTime utc, IGameConfig? gameConfig)
    {
        var (text, suffix) = Convert(utc, gameConfig);
        return $"{text:ddd HH\\:mm} {suffix}";
    }

    private static (DateTime text, string suffix) Convert(DateTime utc, IGameConfig? gameConfig)
    {
        if (TryGetTimeMode(gameConfig, out var mode) && mode == ServerTimeMode)
            return (DateTime.SpecifyKind(utc, DateTimeKind.Utc), "ST");
        return (utc.ToLocalTime(), "LT");
    }

    private static bool TryGetTimeMode(IGameConfig? gameConfig, out uint mode)
    {
        mode = 0;
        if (gameConfig is null) return false;
        try
        {
            return gameConfig.UiConfig.TryGetUInt(UiConfigOption.TimeMode.ToString(), out mode);
        }
        catch
        {
            return false;
        }
    }
}
