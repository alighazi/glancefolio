using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PortfolioWeb;

// ---------------------------------------------------------------------------
// Quota discipline
// ---------------------------------------------------------------------------
//
// Everything here exists because the app runs on free, key-less tiers:
//
//   open.er-api.com    free, but a monthly request allowance and daily data
//   api.coingecko.com  free, roughly 10-30 calls/minute, hard 429s
//   Yahoo Finance      unofficial, no key, no guarantee, throttles aggressively
//
// So the rules are: never fetch what is already cached; never let two callers
// fetch the same thing at once; serve stale data rather than block or fail;
// keep a per-host minimum spacing between calls; and cap each host with a
// daily budget so a bug cannot burn a month's allowance in an afternoon.
// Whatever happened is counted, and /api/diagnostics exposes the counters -
// quota use should be visible, not mysterious.

/// <summary>Result of a cached provider call.</summary>
public sealed record ProviderResult<T>
{
    public T? Value { get; init; }
    public bool FromCache { get; init; }
    public bool Stale { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? CachedAtUtc { get; init; }

    public bool Ok => Value is not null && Error is null;
}

public sealed class HostStats
{
    public string Host { get; init; } = "";
    public int Requests;
    public int CacheHits;
    public int StaleServes;
    public int Errors;
    public int Throttled;      // had to wait on the token bucket
    public int BudgetBlocked;  // daily budget already spent
    public DateTimeOffset? LastCallUtc;
}

/// <summary>Per-host counters, surfaced through /api/diagnostics.</summary>
public sealed class QuotaMeter
{
    private readonly ConcurrentDictionary<string, HostStats> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public HostStats For(string host) =>
        _hosts.GetOrAdd(host, h => new HostStats { Host = h });

    public void Hit(string host) { var s = For(host); Interlocked.Increment(ref s.CacheHits); }
    public void Request(string host)
    {
        var s = For(host);
        Interlocked.Increment(ref s.Requests);
        s.LastCallUtc = DateTimeOffset.UtcNow;
    }
    public void Stale(string host) { var s = For(host); Interlocked.Increment(ref s.StaleServes); }
    public void Error(string host) { var s = For(host); Interlocked.Increment(ref s.Errors); }
    public void Throttled(string host) { var s = For(host); Interlocked.Increment(ref s.Throttled); }
    public void Blocked(string host) { var s = For(host); Interlocked.Increment(ref s.BudgetBlocked); }

    public IReadOnlyList<HostStats> Snapshot() =>
        _hosts.Values.OrderBy(h => h.Host, StringComparer.Ordinal).ToList();
}

/// <summary>
/// Token bucket enforcing a minimum average spacing between calls to one host,
/// with a small burst so a page load does not serialise into a crawl.
/// </summary>
public sealed class TokenBucket
{
    private readonly double _refillPerSecond;
    private readonly double _burst;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private double _tokens;
    private DateTimeOffset _last = DateTimeOffset.UtcNow;

    public TokenBucket(double requestsPerMinute, double burst)
    {
        _refillPerSecond = requestsPerMinute / 60.0;
        _burst = Math.Max(1.0, burst);
        _tokens = _burst;
    }

    /// <summary>Waits until a token is available. Returns true if it had to wait.</summary>
    public async Task<bool> WaitAsync(CancellationToken ct)
    {
        var waited = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan delay;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = DateTimeOffset.UtcNow;
                _tokens = Math.Min(_burst, _tokens + (now - _last).TotalSeconds * _refillPerSecond);
                _last = now;
                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return waited;
                }
                delay = TimeSpan.FromSeconds((1.0 - _tokens) / _refillPerSecond);
            }
            finally
            {
                _gate.Release();
            }
            waited = true;
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Two-level cache: a concurrent in-memory map in front of one JSON file per
/// key. The file's last-write time is the entry's timestamp, so a restart keeps
/// the cache warm instead of re-hitting every provider on the first request.
/// Values are stored as the raw JSON of T, which keeps the cache inspectable.
/// </summary>
public sealed class DiskCache
{
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Created, string Json)> _mem = new();
    private readonly JsonSerializerOptions _json;

    public DiskCache(string dir, JsonSerializerOptions json)
    {
        _dir = dir;
        _json = json;
        Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Cache file name: a readable slug of the key plus a hash of it. The slug
    /// keeps the directory inspectable ("crypto_top250-4F2A….json") and lets
    /// <see cref="Clear"/> select a family of entries by prefix; the hash keeps
    /// two keys with the same slug from colliding.
    /// </summary>
    private static string Slug(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var ch in key)
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_');
        var s = sb.ToString();
        while (s.Contains("__", StringComparison.Ordinal)) s = s.Replace("__", "_", StringComparison.Ordinal);
        s = s.Trim('_');
        return s.Length > 48 ? s[..48] : s;
    }

    private string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(_dir, Slug(key) + "-" + hash + ".json");
    }

    /// <summary>
    /// Drops cached entries, optionally only those whose key starts with
    /// `keyPrefix`. This is what backs the UI's "refresh now": prices are
    /// re-fetched on demand instead of on a timer, which is the difference
    /// between a quota that lasts and one that does not.
    /// </summary>
    public int Clear(string? keyPrefix = null)
    {
        var removed = 0;
        foreach (var key in _mem.Keys.ToList())
        {
            if (keyPrefix is null || key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (_mem.TryRemove(key, out _)) removed++;
            }
        }

        var slugPrefix = keyPrefix is null ? null : Slug(keyPrefix);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
            {
                var name = Path.GetFileName(file);
                if (slugPrefix is null || name.StartsWith(slugPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); removed++; } catch { /* best effort */ }
                }
            }
        }
        catch { /* directory may not exist yet */ }

        return removed;
    }

    public (bool Found, T? Value, DateTimeOffset Created) Get<T>(string key)
    {
        if (!_mem.TryGetValue(key, out var entry))
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return (false, default, default);
            try
            {
                var created = File.GetLastWriteTimeUtc(path);
                entry = (created, File.ReadAllText(path));
                _mem[key] = entry;
            }
            catch
            {
                return (false, default, default);
            }
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(entry.Json, _json);
            if (value is null) return (false, default, default);
            return (true, value, entry.Created);
        }
        catch
        {
            // A cache file we cannot read is worse than no cache file at all.
            _mem.TryRemove(key, out _);
            try { File.Delete(PathFor(key)); } catch { /* best effort */ }
            return (false, default, default);
        }
    }

    public void Set<T>(string key, T value)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(value, _json);
        }
        catch
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _mem[key] = (now, json);

        // Atomic replace: a crash mid-write must not leave a truncated file
        // that later deserialises into a half-populated cache entry.
        var path = PathFor(key);
        var tmp = path + "." + Guid.NewGuid().ToString("n")[..8] + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>How much of the provider quota the cache is currently holding.</summary>
    public (int Files, long Bytes) Size()
    {
        try
        {
            var files = Directory.GetFiles(_dir, "*.json");
            long bytes = 0;
            foreach (var f in files)
            {
                try { bytes += new FileInfo(f).Length; } catch { /* file may be mid-write */ }
            }
            return (files.Length, bytes);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Drops entries untouched for more than `olderThan`, from both the memory
    /// map and the directory, so neither can grow forever. The two levels are
    /// pruned together: leaving the in-memory copy behind would keep serving a
    /// value the disk cache no longer has.
    /// </summary>
    public int Prune(TimeSpan olderThan)
    {
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        var removed = 0;

        foreach (var kv in _mem)
        {
            if (kv.Value.Created < cutoff && _mem.TryRemove(kv.Key, out _)) removed++;
        }

        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff)
                    {
                        File.Delete(f);
                        removed++;
                    }
                }
                catch { /* ignore individual failures */ }
            }
        }
        catch { /* directory may not exist yet */ }
        return removed;
    }
}

/// <summary>
/// The single entry point for talking to a provider: cache lookup, token
/// bucket, daily budget, HTTP, cache write, and stale-while-revalidate.
/// Services never touch HttpClient directly.
/// </summary>
public sealed class ProviderClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly DiskCache _cache;
    private readonly QuotaMeter _meter;
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inflight = new();
    private readonly ConcurrentDictionary<string, int> _dailyCount = new();
    private readonly ILogger<ProviderClient> _log;
    private readonly TimeSpan _bucketWaitCap;

    public int DailyBudgetPerHost { get; set; } = 2000;

    public ProviderClient(IHttpClientFactory httpFactory, DiskCache cache, QuotaMeter meter,
                          ILogger<ProviderClient> log, TimeSpan? bucketWaitCap = null)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _meter = meter;
        _log = log;
        _bucketWaitCap = bucketWaitCap ?? TimeSpan.FromSeconds(6);
    }

    private TokenBucket BucketFor(string host, double requestsPerMinute, double burst) =>
        _buckets.GetOrAdd(host, _ => new TokenBucket(requestsPerMinute, burst));

    private string DayKey() => DateTime.UtcNow.ToString("yyyy-MM-dd");

    private bool BudgetAvailable(string host)
    {
        var key = host + "|" + DayKey();
        var used = _dailyCount.AddOrUpdate(key, 1, (_, v) => v + 1);
        return used <= DailyBudgetPerHost;
    }

    /// <summary>
    /// GET `url` and deserialise JSON into T.
    ///
    /// Fresh cache  -> served, no request.
    /// Expired but within `staleTtl` -> served immediately and refreshed in the
    ///   background, so the UI never waits on a provider it does not have to.
    /// No usable cache -> request, subject to the token bucket and daily budget.
    /// Request fails but something is cached -> serve it flagged stale.
    /// </summary>
    /// <param name="maxWait">
    /// How long this call may wait for a rate-limit token. Interactive callers
    /// pass a short value so a busy host degrades into "no results from this
    /// source" instead of a multi-second stall behind a keystroke.
    /// </param>
    public async Task<ProviderResult<T>> GetJsonAsync<T>(
        string url, string cacheKey, TimeSpan ttl, TimeSpan staleTtl,
        string host, double requestsPerMinute, double burst,
        CancellationToken ct = default, TimeSpan? maxWait = null) where T : class
    {
        var stats = _meter.For(host);
        var (found, cached, created) = _cache.Get<T>(cacheKey);
        var age = found ? DateTimeOffset.UtcNow - created : TimeSpan.MaxValue;

        if (found && age <= ttl)
        {
            _meter.Hit(host);
            return new ProviderResult<T> { Value = cached, FromCache = true, CachedAtUtc = created };
        }

        if (found && age <= ttl + staleTtl)
        {
            _meter.Stale(host);
            _ = RefreshInBackgroundAsync<T>(url, cacheKey, host, requestsPerMinute, burst);
            return new ProviderResult<T>
            {
                Value = cached, FromCache = true, Stale = true, CachedAtUtc = created,
                Error = null
            };
        }

        var fetched = await FetchAsync<T>(url, cacheKey, host, requestsPerMinute, burst, ct, maxWait).ConfigureAwait(false);
        if (fetched.Ok) return fetched;

        // The network failed. Anything at all in the cache beats nothing, even
        // if it is old - flagged so the UI can say how old.
        if (found)
        {
            _meter.Stale(host);
            _log.LogWarning("using {Age} old cache for {Host} after failure: {Error}",
                            age, host, fetched.Error);
            return new ProviderResult<T>
            {
                Value = cached, FromCache = true, Stale = true, CachedAtUtc = created,
                Error = fetched.Error
            };
        }

        _meter.Error(host);
        return fetched;
    }

    private async Task<ProviderResult<T>> FetchAsync<T>(
        string url, string cacheKey, string host, double requestsPerMinute, double burst,
        CancellationToken ct, TimeSpan? maxWait = null) where T : class
    {
        if (!BudgetAvailable(host))
        {
            _meter.Blocked(host);
            return new ProviderResult<T> { Error = $"daily request budget for {host} exhausted" };
        }

        var bucket = BucketFor(host, requestsPerMinute, burst);
        bool waited;
        try
        {
            // Do not let a slow host wedge a UI request forever: if the bucket
            // would make us wait too long, give up and let the caller fall back
            // to stale data.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(maxWait ?? _bucketWaitCap);
            try
            {
                waited = await bucket.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _meter.Throttled(host);
                return new ProviderResult<T> { Error = $"{host} is rate limited right now" };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        if (waited) _meter.Throttled(host);

        _meter.Request(host);
        try
        {
            var client = _httpFactory.CreateClient("providers");
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);

            if ((int)response.StatusCode is 429 or 503)
            {
                _meter.Error(host);
                return new ProviderResult<T> { Error = $"{host} replied {(int)response.StatusCode} (rate limited)" };
            }
            if (!response.IsSuccessStatusCode)
            {
                _meter.Error(host);
                return new ProviderResult<T> { Error = $"{host} replied {(int)response.StatusCode}" };
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            T? value;
            try
            {
                value = JsonSerializer.Deserialize<T>(body, _jsonOptions);
            }
            catch (JsonException ex)
            {
                _meter.Error(host);
                return new ProviderResult<T> { Error = $"{host} returned unparseable JSON ({ex.Message})" };
            }
            if (value is null)
            {
                _meter.Error(host);
                return new ProviderResult<T> { Error = $"{host} returned an empty body" };
            }

            _cache.Set(cacheKey, value);
            return new ProviderResult<T> { Value = value, CachedAtUtc = DateTimeOffset.UtcNow };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _meter.Error(host);
            _log.LogWarning(ex, "request to {Host} failed", host);
            return new ProviderResult<T> { Error = $"{host}: {ex.Message}" };
        }
    }

    private static readonly JsonSerializerOptions _jsonOptions = Json.Options;

    // One background refresh per cache key at a time; a second caller finding
    // the same key in flight just returns the stale value it already has.
    private Task RefreshInBackgroundAsync<T>(string url, string cacheKey, string host,
                                             double requestsPerMinute, double burst) where T : class
    {
        if (!_inflight.TryAdd(cacheKey, 0)) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try
            {
                await FetchAsync<T>(url, cacheKey, host, requestsPerMinute, burst, CancellationToken.None, maxWait: null)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "background refresh of {Key} failed", cacheKey);
            }
            finally
            {
                _inflight.TryRemove(cacheKey, out _);
            }
        });
    }
}
