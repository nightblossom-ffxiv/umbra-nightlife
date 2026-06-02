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
                    I18N("Default shows venues across your whole region (e.g. EU = Chaos + Light). Pick a region, a single data centre, your current DC only, or 'All'."),
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

    /// <summary>
    /// Physical region → its logical data centres. Used to show venues across a player's whole
    /// region (an EU player sees both Chaos and Light) rather than just their current DC.
    /// Keys match the "region:" dropdown values; names match the FFXIVVenues <c>dataCenter</c> field.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> RegionDataCenters =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["NA"] = ["Aether", "Primal", "Crystal", "Dynamis"],
            ["EU"] = ["Chaos", "Light"],
            ["JP"] = ["Elemental", "Gaia", "Mana", "Meteor"],
            ["OCE"] = ["Materia"],
        };

    private static Dictionary<string, string> DataCentreOptions()
        => new()
        {
            [""] = "Auto (your whole region)",
            ["CurrentDc"] = "Your current data centre only",
            ["All"] = "All regions",
            ["region:NA"] = "North America (all DCs)",
            ["region:EU"] = "Europe (Chaos + Light)",
            ["region:JP"] = "Japan (all DCs)",
            ["region:OCE"] = "Oceania (Materia)",
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
            if (dcFilter is not null && !dcFilter.Contains(v.DataCenter)) continue;
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
        });
    }

    private MenuPopup.Button BuildVenueButton(VenueView v, bool isFavorite = false)
    {
        var label = isFavorite ? $"★ {v.Name}" : v.Name;

        var time = v.IsOpenNow
            ? $"closes {FormatTime(v.CurrentCloseAtUtc!.Value)}"
            : v.NextOpenAtUtc is not null
                ? $"opens {FormatNextOpen(v.NextOpenAtUtc.Value)}"
                : "no upcoming session";

        // Show the first 2 tags as a quick "what kind of place" hint.
        var tagSummary = v.Tags.Count switch
        {
            0 => "",
            1 => $"{v.Tags[0]} · ",
            _ => $"{v.Tags[0]}, {v.Tags[1]} · ",
        };

        return new MenuPopup.Button(label)
        {
            OnClick = () => HandleVenueClick(v),
            AltText = $"{tagSummary}{v.DataCenter}/{v.World} · {time}",
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
    /// Resolves the configured filter to the SET of data-centre names whose venues should show,
    /// or <c>null</c> for "no filter / show everything".
    ///   <c>""</c>         → auto: every DC in the player's physical region (EU ⇒ Chaos + Light);
    ///                       falls back to no filter when not logged in.
    ///   <c>"CurrentDc"</c> → only the player's current DC (the old auto behaviour).
    ///   <c>"All"</c>       → null (no filter).
    ///   <c>"region:XX"</c> → every DC in that region.
    ///   <c>"&lt;DC&gt;"</c>      → that single DC.
    /// </summary>
    private IReadOnlySet<string>? ResolveDataCenterFilter()
    {
        var setting = GetConfigValue<string>("DataCenter") ?? "";

        if (setting == "All") return null;

        if (setting.StartsWith("region:", StringComparison.Ordinal))
        {
            var region = setting["region:".Length..];
            return RegionDataCenters.TryGetValue(region, out var dcs) ? ToSet(dcs) : null;
        }

        if (setting == "CurrentDc")
        {
            var dc = CurrentPlayerDataCenter();
            return dc is null ? null : ToSet(dc);
        }

        if (setting != "")                 // an explicit single DC
            return ToSet(setting);

        // "" = auto → the player's whole region.
        var current = CurrentPlayerDataCenter();
        if (current is null) return null;                       // not logged in → show all
        return RegionForDataCenter(current) ?? ToSet(current);  // unknown DC → just that DC
    }

    private static IReadOnlySet<string> ToSet(params string[] names)
        => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    /// <summary>Current character's data-centre name, or null if not logged in / unavailable.</summary>
    private static string? CurrentPlayerDataCenter()
    {
        try
        {
            var world = ObjectTable.LocalPlayer?.CurrentWorld.ValueNullable;
            var dcName = world?.DataCenter.ValueNullable?.Name.ExtractText();
            return string.IsNullOrWhiteSpace(dcName) ? null : dcName;
        }
        catch
        {
            return null; // not logged in or API shape changed
        }
    }

    /// <summary>All data centres in the same region as <paramref name="dc"/>, or null if unknown.</summary>
    private static IReadOnlySet<string>? RegionForDataCenter(string dc)
    {
        foreach (var (_, dcs) in RegionDataCenters)
            if (dcs.Contains(dc, StringComparer.OrdinalIgnoreCase))
                return ToSet(dcs);
        return null;
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
