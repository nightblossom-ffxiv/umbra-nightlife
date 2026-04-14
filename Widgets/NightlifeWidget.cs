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
/// data centre, and opens a menu listing them with one-click teleport.
///
/// Data comes from <see href="https://api.ffxivvenues.com/">FFXIVVenues</see>.
/// We do not host or aggregate the data; the widget is purely a client.
///
/// Click a venue to teleport via Lifestream.
/// <b>Ctrl+Click</b> to pin it to a Favorites section at the top.
/// <b>Shift+Click</b> to hide it; the footer lets you restore hidden venues.
/// All times are shown in Server Time (ST).
/// </summary>
[ToolbarWidget(
    "NightlifeWidget",
    "Tonight in Eorzea",
    "Discover FFXIV venues that are open right now. Click to teleport via Lifestream. Ctrl+Click to favorite, Shift+Click to hide. Data by ffxivvenues.com."
)]
public class NightlifeWidget(
    WidgetInfo info,
    string? guid = null,
    Dictionary<string, object>? configValues = null
) : StandardToolbarWidget(info, guid, configValues)
{
    private static IDalamudPluginInterface PluginInterface => Framework.DalamudPlugin;
    private static ICommandManager CommandManager => Framework.Service<ICommandManager>();
    private static IChatGui ChatGui => Framework.Service<IChatGui>();
    private static IPluginLog Log => Framework.Service<IPluginLog>();
    private static IKeyState KeyState => Framework.Service<IKeyState>();
    private static IObjectTable ObjectTable => Framework.Service<IObjectTable>();

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
                    I18N("Default shows only venues on your current data centre. Choose a specific DC or 'All' to override."),
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
            [""] = "Auto (your current data centre)",
            ["All"] = "All data centres",
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
        SetGameIconId(63934);
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

        var dcFilter = ResolveDataCenterFilter();
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

        // Favorites bypass filters — pinning wins.
        var views = new List<VenueView>(source.Count);
        var favoriteViews = new List<VenueView>();
        foreach (var dto in source)
        {
            var v = VenueProjection.Project(dto, nowUtc);
            if (v is null) continue;

            if (_preferences.IsFavorite(v.Id)) { favoriteViews.Add(v); continue; }
            if (_preferences.IsHidden(v.Id)) continue;
            if (dcFilter is not null && v.DataCenter != dcFilter) continue;
            if (sfwOnly && !v.Sfw) continue;
            if (openOnly && !v.IsOpenNow) continue;
            views.Add(v);
        }

        views.Sort(VenueComparer.Instance);
        favoriteViews.Sort(VenueComparer.Instance);

        var liveCount = views.Count(v => v.IsOpenNow) + favoriteViews.Count(v => v.IsOpenNow);
        var totalCount = views.Count + favoriteViews.Count;
        SetText(liveCount > 0 ? $"{liveCount} live" : "Tonight");
        SetSubText(totalCount == 0 ? "No matches" : $"{totalCount} venues");

        if (favoriteViews.Count > 0)
        {
            var group = new MenuPopup.Group($"★ Favorites ({favoriteViews.Count})");
            foreach (var v in favoriteViews) group.Add(BuildVenueButton(v, isFavorite: true));
            Popup.Add(group);
        }

        if (views.Count == 0 && favoriteViews.Count == 0)
        {
            Popup.Add(new MenuPopup.Header("No venues match your current filters."));
            return;
        }

        var liveVenues = views.Where(v => v.IsOpenNow).Take(maxItems).ToList();
        if (liveVenues.Count > 0)
        {
            var group = new MenuPopup.Group($"● Open now ({liveVenues.Count})");
            foreach (var v in liveVenues) group.Add(BuildVenueButton(v));
            Popup.Add(group);
        }

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

        var hiddenCount = _preferences.Hidden.Count;
        if (hiddenCount > 0)
        {
            Popup.Add(new MenuPopup.Button($"Show {hiddenCount} hidden venue(s)")
            {
                OnClick = () =>
                {
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
        var label = isFavorite ? $"★ {v.Name}" : v.Name;

        var time = v.IsOpenNow
            ? $"closes {FormatTime(v.CurrentCloseAtUtc!.Value)}"
            : v.NextOpenAtUtc is not null
                ? $"opens {FormatNextOpen(v.NextOpenAtUtc.Value)}"
                : "schedule unknown";

        return new MenuPopup.Button(label)
        {
            OnClick = () => HandleVenueClick(v),
            Icon = v.IsOpenNow ? 60045u : 60046u,
            AltText = $"{v.DataCenter}/{v.World} · {time}",
            // Keep the popup open so Ctrl/Shift batches don't slam it shut after each click.
            ClosePopupOnClick = false,
        };
    }

    private void HandleVenueClick(VenueView v)
    {
        if (_preferences is null) return;

        var ctrl = KeyState[VirtualKey.CONTROL];
        var shift = KeyState[VirtualKey.SHIFT];

        if (ctrl)
        {
            _preferences.ToggleFavorite(v.Id);
            ChatGui.Print($"[Nightlife] {v.Name} {(_preferences.IsFavorite(v.Id) ? "★ favorited" : "unfavorited")}.");
            // Keep the popup open so the user can ★ or hide several venues in a row.
        }
        else if (shift)
        {
            _preferences.ToggleHidden(v.Id);
            ChatGui.Print($"[Nightlife] {v.Name} {(_preferences.IsHidden(v.Id) ? "hidden" : "unhidden")}.");
        }
        else
        {
            _lifestream?.TeleportTo(v);
            // Plain click is a teleport — close the menu so it doesn't linger over the gameplay view.
            Popup.Close();
        }

        _lastRebuildAtUtc = DateTime.MinValue;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// "" = auto (current DC, or null if not logged in → no filter).
    /// "All" = explicit no filter.
    /// DC name = that DC only.
    /// Returns null when no filtering should happen.
    /// </summary>
    private string? ResolveDataCenterFilter()
    {
        var setting = GetConfigValue<string>("DataCenter") ?? "";
        if (setting == "All") return null;
        if (setting != "") return setting;

        try
        {
            var world = ObjectTable.LocalPlayer?.CurrentWorld.ValueNullable;
            var dcName = world?.DataCenter.ValueNullable?.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(dcName)) return dcName;
        }
        catch
        {
            // Not logged in or API shape changed — fall through.
        }
        return null; // no DC detected → show all
    }

    private static string FormatTime(DateTime utc)
        => $"{utc:HH\\:mm} ST";

    private static string FormatNextOpen(DateTime futureUtc)
    {
        var delta = futureUtc - DateTime.UtcNow;
        var absolute = delta.TotalHours < 24
            ? $"{futureUtc:HH\\:mm} ST"
            : $"{futureUtc:ddd HH\\:mm} ST";

        var relative = delta.TotalMinutes < 1 ? "now"
                     : delta.TotalMinutes < 60 ? $"in {(int)delta.TotalMinutes}m"
                     : delta.TotalHours < 24  ? $"in {(int)delta.TotalHours}h"
                     : $"in {(int)delta.TotalDays}d";

        return $"{absolute} ({relative})";
    }

    private static string I18N(string key) => key;

    private sealed class VenueComparer : IComparer<VenueView>
    {
        public static readonly VenueComparer Instance = new();

        public int Compare(VenueView? a, VenueView? b)
        {
            if (a is null || b is null) return 0;
            var aLive = a.IsOpenNow ? 0 : 1;
            var bLive = b.IsOpenNow ? 0 : 1;
            if (aLive != bLive) return aLive - bLive;

            var aNext = a.NextOpenAtUtc ?? DateTime.MaxValue;
            var bNext = b.NextOpenAtUtc ?? DateTime.MaxValue;
            var cmp = aNext.CompareTo(bNext);
            if (cmp != 0) return cmp;

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
