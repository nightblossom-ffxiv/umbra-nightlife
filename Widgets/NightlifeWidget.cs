using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Umbra.Common;
using Umbra.Widgets;
using UmbraNightlife.Models;
using UmbraNightlife.Services;

namespace UmbraNightlife.Widgets;

/// <summary>
/// Toolbar widget "Tonight in Eorzea" — shows how many venues are live on your
/// selected data centre, and opens a menu listing them with one-click teleport.
///
/// Data comes from <see href="https://api.ffxivvenues.com/">FFXIVVenues</see>.
/// We do not host or aggregate the data; the widget is purely a client.
/// </summary>
[ToolbarWidget(
    "NightlifeWidget",
    "Tonight in Eorzea",
    "Discover FFXIV venues that are open right now. Click a venue to teleport via Lifestream. Data by ffxivvenues.com."
)]
public class NightlifeWidget(
    WidgetInfo info,
    string? guid = null,
    Dictionary<string, object>? configValues = null
) : StandardToolbarWidget(info, guid, configValues)
{
    // Umbra's framework gives us singletons; our services are created per-widget on load.
    private static readonly IDalamudPluginInterface PluginInterface = Framework.Service<IDalamudPluginInterface>();
    private static readonly ICommandManager CommandManager = Framework.Service<ICommandManager>();
    private static readonly IChatGui ChatGui = Framework.Service<IChatGui>();
    private static readonly IPluginLog Log = Framework.Service<IPluginLog>();

    private FfxivVenuesClient? _client;
    private LifestreamBridge? _lifestream;

    protected override StandardWidgetFeatures Features =>
        StandardWidgetFeatures.Text |
        StandardWidgetFeatures.SubText |
        StandardWidgetFeatures.Icon |
        StandardWidgetFeatures.CustomizableIcon;

    public override MenuPopup Popup { get; } = new();

    private DateTime _lastRebuildAtUtc = DateTime.MinValue;
    private static readonly TimeSpan RebuildInterval = TimeSpan.FromSeconds(30);

    // ── Config variables ────────────────────────────────────────────────

    protected override IEnumerable<IWidgetConfigVariable> GetConfigVariables()
    {
        return
        [
            ..base.GetConfigVariables(),

            new SelectWidgetConfigVariable(
                    "DataCenter",
                    I18N("Data Centre"),
                    I18N("Filter to a single data centre, or show venues from all data centres."),
                    "",
                    DataCentreOptions()
                ) { Category = "Filters" },

            new BooleanWidgetConfigVariable(
                    "OpenOnly",
                    I18N("Show open venues only"),
                    I18N("Hide venues that are not open right now."),
                    true
                ) { Category = "Filters" },

            new BooleanWidgetConfigVariable(
                    "SfwOnly",
                    I18N("Hide NSFW venues"),
                    I18N("Exclude venues that are not marked safe for work."),
                    true
                ) { Category = "Filters" },

            new IntegerWidgetConfigVariable(
                    "MaxItems",
                    I18N("Max items in menu"),
                    I18N("How many venues to list in the drop-down before truncating."),
                    30, 5, 100
                ) { Category = "Display" },
        ];
    }

    private static Dictionary<string, string> DataCentreOptions()
        => new()
        {
            [""] = "All data centres",
            ["Aether"] = "Aether (NA)",
            ["Primal"] = "Primal (NA)",
            ["Crystal"] = "Crystal (NA)",
            ["Dynamis"] = "Dynamis (NA)",
            ["Chaos"] = "Chaos (EU)",
            ["Light"] = "Light (EU)",
            ["Materia"] = "Materia (OCE)",
            ["Mana"] = "Mana (JP)",
            ["Gaia"] = "Gaia (JP)",
            ["Elemental"] = "Elemental (JP)",
            ["Meteor"] = "Meteor (JP)",
        };

    // ── Lifecycle ───────────────────────────────────────────────────────

    protected override void OnLoad()
    {
        SetGameIconId(63934); // moon-ish icon; users can swap via CustomizableIcon.
        SetText("Tonight");

        _client = new FfxivVenuesClient(PluginInterface, Log);
        _lifestream = new LifestreamBridge(PluginInterface, CommandManager, ChatGui, Log);

        _ = _client.InitializeAsync(System.Threading.CancellationToken.None);
    }

    protected override void OnDraw()
    {
        if (_client is null) return;

        // Rebuild menu at most every 30 seconds; avoids jitter while Popup is open.
        if (DateTime.UtcNow - _lastRebuildAtUtc < RebuildInterval) return;

        RebuildPopup();
        _lastRebuildAtUtc = DateTime.UtcNow;
    }

    protected override void OnUnload()
    {
        _client?.Dispose();
        _lifestream?.Dispose();
        _client = null;
        _lifestream = null;
    }

    // ── Menu construction ───────────────────────────────────────────────

    private void RebuildPopup()
    {
        if (_client is null) return;

        Popup.Clear();

        var dc = GetConfigValue<string>("DataCenter") ?? "";
        var openOnly = GetConfigValue<bool>("OpenOnly");
        var sfwOnly = GetConfigValue<bool>("SfwOnly");
        var maxItems = Math.Clamp(GetConfigValue<int>("MaxItems"), 5, 100);

        var nowUtc = DateTime.UtcNow;
        var source = _client.GetOrEmpty();

        if (source.Count == 0)
        {
            var age = nowUtc - _client.LastFetchedAtUtc;
            var msg = _client.HasData
                ? $"No venues match your filters (catalog age {age:hh\\:mm})."
                : "Loading venues from ffxivvenues.com…";
            SetSubText(msg);
            Popup.Add(new MenuPopup.Header(msg));
            return;
        }

        var views = new List<VenueView>(source.Count);
        foreach (var dto in source)
        {
            var v = VenueProjection.Project(dto, nowUtc);
            if (v is null) continue;
            if (!string.IsNullOrEmpty(dc) && v.DataCenter != dc) continue;
            if (sfwOnly && !v.Sfw) continue;
            if (openOnly && !v.IsOpenNow) continue;
            views.Add(v);
        }

        views.Sort(VenueComparer.Instance);

        var liveCount = views.Count(v => v.IsOpenNow);
        SetText(liveCount > 0 ? $"{liveCount} live" : "Tonight");
        SetSubText(views.Count == 0 ? "No matches" : $"{views.Count} venues");

        if (views.Count == 0)
        {
            Popup.Add(new MenuPopup.Header("No venues match your current filters."));
            return;
        }

        // ── Section: Open now ─────────────────────────────────────
        var liveVenues = views.Where(v => v.IsOpenNow).Take(maxItems).ToList();
        if (liveVenues.Count > 0)
        {
            var group = new MenuPopup.Group($"● Open now ({liveVenues.Count})");
            foreach (var v in liveVenues) group.Add(BuildVenueButton(v));
            Popup.Add(group);
        }

        // ── Section: Opening soon ─────────────────────────────────
        var remaining = maxItems - liveVenues.Count;
        if (remaining > 0)
        {
            var soon = views
                .Where(v => !v.IsOpenNow && v.NextOpenAtUtc is not null)
                .Take(remaining)
                .ToList();
            if (soon.Count > 0)
            {
                var group = new MenuPopup.Group("Opening soon");
                foreach (var v in soon) group.Add(BuildVenueButton(v));
                Popup.Add(group);
            }
        }

        // ── Footer actions ────────────────────────────────────────
        Popup.Add(new MenuPopup.Button("Refresh catalogue")
        {
            OnClick = () =>
            {
                _ = _client.RefreshAsync(System.Threading.CancellationToken.None);
                _lastRebuildAtUtc = DateTime.MinValue;
            },
            Icon = 60033u, // refresh glyph
        });
    }

    private MenuPopup.Button BuildVenueButton(VenueView v)
    {
        var altText = v.IsOpenNow
            ? $"{v.DataCenter}/{v.World} · closes {FormatHm(v.CurrentCloseAtUtc)}"
            : v.NextOpenAtUtc is not null
                ? $"{v.DataCenter}/{v.World} · opens {FormatRelative(v.NextOpenAtUtc.Value)}"
                : $"{v.DataCenter}/{v.World}";

        return new MenuPopup.Button(v.Name)
        {
            OnClick = () => _lifestream?.TeleportTo(v),
            Icon = v.IsOpenNow ? 60045u : 60046u, // filled vs hollow circle-ish
            AltText = altText,
        };
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string FormatHm(DateTime? utc)
    {
        if (utc is null) return "";
        var local = utc.Value.ToLocalTime();
        return local.ToString("HH:mm");
    }

    private static string FormatRelative(DateTime futureUtc)
    {
        var delta = futureUtc - DateTime.UtcNow;
        if (delta.TotalMinutes < 1) return "now";
        if (delta.TotalMinutes < 60) return $"in {(int)delta.TotalMinutes}m";
        if (delta.TotalHours < 24) return $"in {(int)delta.TotalHours}h {(int)(delta.TotalMinutes % 60)}m";
        return futureUtc.ToLocalTime().ToString("ddd HH:mm");
    }

    private static string I18N(string key) => key; // Swap in Umbra.Common's I18N helper when we localise.

    private sealed class VenueComparer : IComparer<VenueView>
    {
        public static readonly VenueComparer Instance = new();

        public int Compare(VenueView? a, VenueView? b)
        {
            if (a is null || b is null) return 0;
            // Open now first.
            var aLive = a.IsOpenNow ? 0 : 1;
            var bLive = b.IsOpenNow ? 0 : 1;
            if (aLive != bLive) return aLive - bLive;

            // Among closed, earliest upcoming first.
            var aNext = a.NextOpenAtUtc ?? DateTime.MaxValue;
            var bNext = b.NextOpenAtUtc ?? DateTime.MaxValue;
            var cmp = aNext.CompareTo(bNext);
            if (cmp != 0) return cmp;

            // Finally alphabetical.
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
