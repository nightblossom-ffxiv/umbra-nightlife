using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game.ClientState.Keys;
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
///
/// Interactions:
/// <list type="bullet">
///   <item><b>Click</b> — teleport via Lifestream.</item>
///   <item><b>Ctrl+Click</b> — toggle favorite (pinned at top).</item>
///   <item><b>Shift+Click</b> — toggle hide (won't show in the main list).</item>
/// </list>
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
    // Umbra exposes Dalamud services via Framework.Service<T>, and the plugin
    // interface itself via Framework.DalamudPlugin (it is not in the service
    // container because sub-plugins shouldn't have full plugin power).
    private static IDalamudPluginInterface PluginInterface => Framework.DalamudPlugin;
    private static ICommandManager CommandManager => Framework.Service<ICommandManager>();
    private static IChatGui ChatGui => Framework.Service<IChatGui>();
    private static IPluginLog Log => Framework.Service<IPluginLog>();
    private static IKeyState KeyState => Framework.Service<IKeyState>();
    private static IGameConfig GameConfig => Framework.Service<IGameConfig>();

    private FfxivVenuesClient? _client;
    private LifestreamBridge? _lifestream;
    private FavoritesStore? _preferences;

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

        var configDir = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "UmbraNightlife");
        _client = new FfxivVenuesClient(PluginInterface, Log);
        _lifestream = new LifestreamBridge(PluginInterface, CommandManager, ChatGui, Log);
        _preferences = new FavoritesStore(configDir, Log);

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
        _preferences = null;
    }

    // ── Menu construction ───────────────────────────────────────────────

    private void RebuildPopup()
    {
        if (_client is null || _preferences is null) return;

        Popup.Clear();

        var dc = GetConfigValue<string>("DataCenter") ?? "";
        var openOnly = GetConfigValue<bool>("OpenOnly");
        var sfwOnly = GetConfigValue<bool>("SfwOnly");
        var maxItems = Math.Clamp(GetConfigValue<int>("MaxItems"), 5, 100);

        var nowUtc = DateTime.UtcNow;
        var source = _client.GetOrEmpty();

        if (source.Count == 0)
        {
            var msg = _client.HasData
                ? "No venues match your filters."
                : "Loading venues from ffxivvenues.com…";
            SetSubText(msg);
            Popup.Add(new MenuPopup.Header(msg));
            return;
        }

        // Build view list with filters applied. Favorites are always shown, even when
        // they fail the open-only / SFW / DC filters — pinning wins.
        var views = new List<VenueView>(source.Count);
        var favoriteViews = new List<VenueView>();
        foreach (var dto in source)
        {
            var v = VenueProjection.Project(dto, nowUtc);
            if (v is null) continue;

            if (_preferences.IsFavorite(v.Id))
            {
                favoriteViews.Add(v);
                continue;
            }
            if (_preferences.IsHidden(v.Id)) continue;
            if (!string.IsNullOrEmpty(dc) && v.DataCenter != dc) continue;
            if (sfwOnly && !v.Sfw) continue;
            if (openOnly && !v.IsOpenNow) continue;
            views.Add(v);
        }

        views.Sort(VenueComparer.Instance);
        favoriteViews.Sort(VenueComparer.Instance);

        var liveCount = views.Count(v => v.IsOpenNow) + favoriteViews.Count(v => v.IsOpenNow);
        SetText(liveCount > 0 ? $"{liveCount} live" : "Tonight");
        SetSubText(views.Count == 0 && favoriteViews.Count == 0 ? "No matches" : $"{views.Count + favoriteViews.Count} venues");

        // ── Header hint ──────────────────────────────────────────────
        Popup.Add(new MenuPopup.Header(
            "Click = teleport · Ctrl+Click = ⭐ favorite · Shift+Click = hide"));

        // ── Section: Favorites ───────────────────────────────────────
        if (favoriteViews.Count > 0)
        {
            var group = new MenuPopup.Group($"⭐ Favorites ({favoriteViews.Count})");
            foreach (var v in favoriteViews) group.Add(BuildVenueButton(v, isFavorite: true));
            Popup.Add(group);
        }

        if (views.Count == 0 && favoriteViews.Count == 0)
        {
            Popup.Add(new MenuPopup.Header("No venues match your current filters."));
            return;
        }

        // ── Section: Open now ────────────────────────────────────────
        var liveVenues = views.Where(v => v.IsOpenNow).Take(maxItems).ToList();
        if (liveVenues.Count > 0)
        {
            var group = new MenuPopup.Group($"● Open now ({liveVenues.Count})");
            foreach (var v in liveVenues) group.Add(BuildVenueButton(v));
            Popup.Add(group);
        }

        // ── Section: Opening soon ────────────────────────────────────
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

        // ── Footer actions ───────────────────────────────────────────
        var hiddenCount = _preferences.Hidden.Count;
        if (hiddenCount > 0)
        {
            Popup.Add(new MenuPopup.Button($"Show {hiddenCount} hidden venue(s)")
            {
                OnClick = () =>
                {
                    // Clear hidden — one-tap restore of everything.
                    foreach (var id in _preferences.Hidden.ToList()) _preferences.ToggleHidden(id);
                    _lastRebuildAtUtc = DateTime.MinValue;
                },
                Icon = 60041u,
            });
        }

        Popup.Add(new MenuPopup.Button("Refresh catalogue")
        {
            OnClick = () =>
            {
                _ = _client.RefreshAsync(System.Threading.CancellationToken.None);
                _lastRebuildAtUtc = DateTime.MinValue;
            },
            Icon = 60033u,
        });
    }

    private MenuPopup.Button BuildVenueButton(VenueView v, bool isFavorite = false)
    {
        var label = isFavorite ? $"⭐ {v.Name}" : v.Name;

        var time = v.IsOpenNow
            ? $"closes {TimeDisplay.Format(v.CurrentCloseAtUtc!.Value, GameConfig)}"
            : v.NextOpenAtUtc is not null
                ? $"opens {FormatNextOpen(v.NextOpenAtUtc.Value)}"
                : "schedule unknown";

        var altText = $"{v.DataCenter}/{v.World} · {time}\n"
                    + $"Click = teleport  ·  Ctrl+Click = {(isFavorite ? "unfavorite" : "favorite")}  ·  Shift+Click = hide";

        return new MenuPopup.Button(label)
        {
            OnClick = () => HandleVenueClick(v),
            Icon = v.IsOpenNow ? 60045u : 60046u,
            AltText = altText,
        };
    }

    private void HandleVenueClick(VenueView v)
    {
        if (_preferences is null) return;

        // Check modifier keys at click time; Umbra's MenuPopup.Button
        // doesn't carry modifier info in the callback.
        var ctrl = KeyState[VirtualKey.CONTROL];
        var shift = KeyState[VirtualKey.SHIFT];

        if (ctrl)
        {
            _preferences.ToggleFavorite(v.Id);
            ChatGui.Print($"[Nightlife] {v.Name} {(_preferences.IsFavorite(v.Id) ? "★ favorited" : "unfavorited")}.");
        }
        else if (shift)
        {
            _preferences.ToggleHidden(v.Id);
            ChatGui.Print($"[Nightlife] {v.Name} {(_preferences.IsHidden(v.Id) ? "hidden" : "unhidden")}.");
        }
        else
        {
            _lifestream?.TeleportTo(v);
        }

        _lastRebuildAtUtc = DateTime.MinValue; // force rebuild on next draw
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private string FormatNextOpen(DateTime futureUtc)
    {
        var delta = futureUtc - DateTime.UtcNow;
        var absolute = delta.TotalHours < 24
            ? TimeDisplay.Format(futureUtc, GameConfig)
            : TimeDisplay.FormatWithDay(futureUtc, GameConfig);

        var relative = delta.TotalMinutes < 1 ? "now"
                     : delta.TotalMinutes < 60 ? $"in {(int)delta.TotalMinutes}m"
                     : delta.TotalHours < 24  ? $"in {(int)delta.TotalHours}h"
                     : $"in {(int)delta.TotalDays}d";

        return $"{absolute} ({relative})";
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
