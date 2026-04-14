using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using UmbraNightlife.Models;

namespace UmbraNightlife.Services;

/// <summary>
/// Fetches the venue catalog from <c>https://api.ffxivvenues.com/venue</c>.
///
/// Caching: the catalog is persisted to disk (Umbra config dir) and refetched
/// at most once per <see cref="MinRefreshInterval"/>. On network failure, the
/// stale cached copy is returned so the UI never loses the list due to a blip.
/// </summary>
public sealed class FfxivVenuesClient : IDisposable
{
    private const string EndpointUrl = "https://api.ffxivvenues.com/venue";
    private const string CacheFileName = "ffxivvenues-cache.json";
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly IDalamudPluginInterface _pi;
    private readonly IPluginLog _log;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly string _cachePath;

    private List<FfxivVenueDto>? _cached;
    private DateTime _lastSuccessfulFetchUtc = DateTime.MinValue;

    public FfxivVenuesClient(IDalamudPluginInterface pi, IPluginLog log)
    {
        _pi = pi;
        _log = log;
        // Sub-folder under Umbra's plugin-config dir so we don't pollute Umbra's own files.
        _cachePath = Path.Combine(_pi.GetPluginConfigDirectory(), "UmbraNightlife", CacheFileName);

        _http = new HttpClient
        {
            Timeout = RequestTimeout,
            DefaultRequestHeaders =
            {
                { "User-Agent", "UmbraNightlife/1.0 (+https://github.com/nightblossom-ffxiv/nightblossom-helper)" },
                { "Accept", "application/json" },
            },
        };
    }

    /// <summary>True once the first successful fetch (or disk load) has populated the cache.</summary>
    public bool HasData => _cached is not null;

    /// <summary>Timestamp of the last successful network fetch (UTC). <c>MinValue</c> if never.</summary>
    public DateTime LastFetchedAtUtc => _lastSuccessfulFetchUtc;

    /// <summary>
    /// Returns the latest known venues. Triggers a background refresh if the cache
    /// is older than <see cref="MinRefreshInterval"/>, but never blocks the caller.
    /// </summary>
    public IReadOnlyList<FfxivVenueDto> GetOrEmpty()
    {
        // Fire-and-forget background refresh when data is stale.
        if (IsStale && !_refreshLock.CurrentCount.Equals(0))
        {
            _ = Task.Run(() => RefreshAsync(CancellationToken.None));
        }

        return _cached ?? (IReadOnlyList<FfxivVenueDto>)Array.Empty<FfxivVenueDto>();
    }

    private bool IsStale => DateTime.UtcNow - _lastSuccessfulFetchUtc > MinRefreshInterval;

    /// <summary>Call once at startup — loads from disk, then kicks off a refresh.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        LoadFromDisk();
        await RefreshAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Force a network refresh now. Safe to call from UI — serialised internally.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!await _refreshLock.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, EndpointUrl);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            res.EnsureSuccessStatusCode();

            var venues = await res.Content.ReadFromJsonAsync<List<FfxivVenueDto>>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (venues is null) throw new InvalidOperationException("FFXIVVenues returned null.");

            _cached = venues;
            _lastSuccessfulFetchUtc = DateTime.UtcNow;
            SaveToDisk(venues);
            _log.Info($"[Nightlife] Fetched {venues.Count} venues from FFXIVVenues.");
        }
        catch (OperationCanceledException)
        {
            // Shutdown path — swallow.
        }
        catch (Exception ex)
        {
            _log.Warning($"[Nightlife] Venue refresh failed ({ex.GetType().Name}: {ex.Message}). Using cached copy.");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var json = File.ReadAllText(_cachePath);
            var venues = JsonSerializer.Deserialize<List<FfxivVenueDto>>(json);
            if (venues is not null)
            {
                _cached = venues;
                var ts = File.GetLastWriteTimeUtc(_cachePath);
                _lastSuccessfulFetchUtc = ts;
                _log.Debug($"[Nightlife] Loaded {venues.Count} venues from disk cache (age {DateTime.UtcNow - ts:g}).");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"[Nightlife] Could not read venue cache: {ex.Message}");
        }
    }

    private void SaveToDisk(List<FfxivVenueDto> venues)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(venues);
            File.WriteAllText(_cachePath, json);
        }
        catch (Exception ex)
        {
            _log.Warning($"[Nightlife] Could not write venue cache: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _refreshLock.Dispose();
    }
}
