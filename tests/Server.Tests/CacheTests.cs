using System.Diagnostics;
using PortfolioWeb;

namespace Server.Tests;

/// <summary>
/// The cache and the token bucket are the whole quota strategy: the free tiers
/// are small, so what keeps the app usable is that a repeat request costs
/// nothing and a burst of them waits instead of getting the key banned.
/// </summary>
public sealed class CacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "portfolio-cache-" + Guid.NewGuid().ToString("n")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private DiskCache NewCache() => new(_dir, Json.Options);

    private sealed record Payload(string Name, int Rank, decimal Price, List<string> Tags);

    /// <summary>
    /// Record equality compares a List by reference, so two payloads that print
    /// identically are not Equal. Compare what actually matters field by field.
    /// </summary>
    private static void SamePayload(Payload expected, Payload? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Name, actual!.Name);
        Assert.Equal(expected.Rank, actual.Rank);
        Assert.Equal(expected.Price, actual.Price);
        Assert.Equal(expected.Tags, actual.Tags);
    }

    [Fact]
    public void Set_then_Get_round_trips_the_value()
    {
        var cache = NewCache();
        var value = new Payload("PAX Gold", 47, 3619.07m, new List<string> { "gold", "tokenised" });

        cache.Set("crypto:top250", value);
        var (found, read, created) = cache.Get<Payload>("crypto:top250");

        Assert.True(found);
        SamePayload(value, read);
        Assert.True((DateTimeOffset.UtcNow - created).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Get_reports_a_miss_for_an_unknown_key()
    {
        var (found, value, created) = NewCache().Get<Payload>("nope:not-here");

        Assert.False(found);
        Assert.Null(value);
        Assert.Equal(default, created);
    }

    [Fact]
    public void The_cache_survives_a_restart()
    {
        // The file's last-write time is the entry's timestamp, so a restart
        // keeps the cache warm instead of re-hitting every provider at once.
        var value = new Payload("Bitcoin", 1, 60_000m, new List<string>());
        NewCache().Set("crypto:prices:bitcoin", value);

        var (found, read, _) = NewCache().Get<Payload>("crypto:prices:bitcoin");

        Assert.True(found);
        SamePayload(value, read);
    }

    [Fact]
    public void Entries_are_stored_as_readable_json_one_file_per_key()
    {
        var cache = NewCache();
        cache.Set("fx:latest-usd", new Payload("Euro", 2, 1.09m, new List<string>()));

        var files = Directory.GetFiles(_dir, "*.json");
        Assert.Single(files);

        // Inspectable by hand, which is the point of not using a binary blob.
        var text = File.ReadAllText(files[0]);
        Assert.Contains("Euro", text, StringComparison.Ordinal);
        Assert.Contains("1.09", text, StringComparison.Ordinal);

        // The file name keeps a slug of the key so `ls` tells you what is what.
        Assert.Contains("fx_latest_usd", Path.GetFileName(files[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_leaves_no_temporary_files_behind()
    {
        var cache = NewCache();
        for (var i = 0; i < 20; i++) cache.Set($"key:{i}", new Payload($"n{i}", i, i, new List<string>()));

        Assert.Equal(20, Directory.GetFiles(_dir, "*.json").Length);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Two_keys_with_the_same_slug_do_not_collide()
    {
        var cache = NewCache();
        cache.Set("yahoo:chart:AAPL", new Payload("apple", 1, 200m, new List<string>()));
        cache.Set("yahoo/chart/AAPL", new Payload("slashy", 1, 200m, new List<string>()));

        // Both slug to "yahoo_chart_aapl"; only the hash keeps them apart.
        Assert.Equal("apple", cache.Get<Payload>("yahoo:chart:AAPL").Value!.Name);
        Assert.Equal("slashy", cache.Get<Payload>("yahoo/chart/AAPL").Value!.Name);
        Assert.Equal(2, Directory.GetFiles(_dir, "*.json").Length);
    }

    [Fact]
    public void Clear_by_prefix_removes_only_that_family()
    {
        var cache = NewCache();
        cache.Set("yahoo:chart:AAPL", new Payload("a", 1, 1m, new List<string>()));
        cache.Set("yahoo:chart:SPY", new Payload("s", 1, 1m, new List<string>()));
        cache.Set("crypto:prices:bitcoin", new Payload("b", 1, 1m, new List<string>()));
        cache.Set("fx:latest-usd", new Payload("f", 1, 1m, new List<string>()));

        // "Refresh now" clears prices but must not throw away the FX table,
        // which only updates once a day anyway.
        Assert.True(cache.Clear("yahoo:chart:") >= 2);

        Assert.False(cache.Get<Payload>("yahoo:chart:AAPL").Found);
        Assert.False(cache.Get<Payload>("yahoo:chart:SPY").Found);
        Assert.True(cache.Get<Payload>("crypto:prices:bitcoin").Found);
        Assert.True(cache.Get<Payload>("fx:latest-usd").Found);
        Assert.Equal(2, cache.Size().Files);
    }

    [Fact]
    public void Clear_without_a_prefix_empties_the_cache()
    {
        var cache = NewCache();
        cache.Set("a", new Payload("a", 1, 1m, new List<string>()));
        cache.Set("b", new Payload("b", 1, 1m, new List<string>()));

        Assert.True(cache.Clear() >= 2);
        Assert.False(cache.Get<Payload>("a").Found);
        Assert.Equal(0, cache.Size().Files);
        Assert.Equal(0, cache.Size().Bytes);
    }

    [Fact]
    public void Size_counts_files_and_bytes()
    {
        var cache = NewCache();
        Assert.Equal((0, 0L), cache.Size());

        cache.Set("one", new Payload("payload", 1, 1.5m, new List<string> { "x" }));
        var (files, bytes) = cache.Size();

        Assert.Equal(1, files);
        Assert.True(bytes > 0, "a written entry has to occupy something");
    }

    [Fact]
    public void A_corrupt_cache_file_is_dropped_not_thrown()
    {
        var cache = NewCache();
        cache.Set("broken", new Payload("good", 1, 1m, new List<string>()));

        var file = Directory.GetFiles(_dir, "*.json").Single();
        File.WriteAllText(file, "{ this is not json");

        // A fresh instance has to read it from disk, which is where it would blow up.
        var (found, value, _) = NewCache().Get<Payload>("broken");

        Assert.False(found);
        Assert.Null(value);
        Assert.False(File.Exists(file), "the unreadable file should be removed");
    }

    [Fact]
    public void A_value_of_the_wrong_shape_is_a_miss_not_a_crash()
    {
        var cache = NewCache();
        cache.Set("typed", new Payload("good", 1, 1m, new List<string>()));

        var (found, value, _) = NewCache().Get<List<int>>("typed");

        Assert.False(found);
        Assert.Null(value);
    }

    [Fact]
    public void Prune_removes_only_entries_older_than_the_cutoff()
    {
        var cache = NewCache();
        cache.Set("fresh", new Payload("fresh", 1, 1m, new List<string>()));
        cache.Set("ancient", new Payload("ancient", 1, 1m, new List<string>()));

        Assert.Equal(0, cache.Prune(TimeSpan.FromHours(1)));   // nothing is old yet
        Assert.Equal(2, cache.Size().Files);

        // Backdate one file, then read it through a fresh instance so the
        // in-memory timestamp comes from the file too.
        var ancientFile = Directory.GetFiles(_dir, "*.json")
            .Single(f => File.ReadAllText(f).Contains("ancient", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(ancientFile, DateTime.UtcNow.AddHours(-3));

        var reopened = NewCache();
        Assert.True(reopened.Get<Payload>("ancient").Found);
        Assert.True(reopened.Get<Payload>("fresh").Found);

        Assert.Equal(2, reopened.Prune(TimeSpan.FromHours(1)));   // memory entry + file

        Assert.False(reopened.Get<Payload>("ancient").Found);
        Assert.True(reopened.Get<Payload>("fresh").Found);
        Assert.Equal(1, reopened.Size().Files);
    }

    [Fact]
    public void The_directory_is_created_on_demand()
    {
        Assert.False(Directory.Exists(_dir));
        NewCache();
        Assert.True(Directory.Exists(_dir));
    }

    [Fact]
    public void Size_and_Clear_tolerate_a_missing_directory()
    {
        var cache = NewCache();
        Directory.Delete(_dir, recursive: true);

        Assert.Equal((0, 0L), cache.Size());
        Assert.Equal(0, cache.Clear("anything"));
        Assert.Equal(0, cache.Prune(TimeSpan.Zero));
    }
}

public class TokenBucketTests
{
    [Fact]
    public async Task A_burst_is_granted_without_waiting()
    {
        var bucket = new TokenBucket(requestsPerMinute: 6_000, burst: 3);
        var sw = Stopwatch.StartNew();

        Assert.False(await bucket.WaitAsync(CancellationToken.None));
        Assert.False(await bucket.WaitAsync(CancellationToken.None));
        Assert.False(await bucket.WaitAsync(CancellationToken.None));

        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 200, $"burst should be immediate, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Once_the_burst_is_spent_the_next_call_waits_and_says_so()
    {
        // 6000/min = 100 tokens per second, so the wait is short enough to test.
        var bucket = new TokenBucket(requestsPerMinute: 6_000, burst: 2);

        Assert.False(await bucket.WaitAsync(CancellationToken.None));
        Assert.False(await bucket.WaitAsync(CancellationToken.None));

        var sw = Stopwatch.StartNew();
        Assert.True(await bucket.WaitAsync(CancellationToken.None));   // had to wait
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 5, "the third call must actually pause");
        Assert.True(sw.ElapsedMilliseconds < 2_000, $"but not for long: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Tokens_refill_over_time()
    {
        var bucket = new TokenBucket(requestsPerMinute: 60_000, burst: 1);   // 1000/s

        Assert.False(await bucket.WaitAsync(CancellationToken.None));
        Assert.True(await bucket.WaitAsync(CancellationToken.None));

        await Task.Delay(60);                                              // ~60 tokens back
        Assert.False(await bucket.WaitAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_burst_below_one_is_clamped_so_nothing_deadlocks()
    {
        var bucket = new TokenBucket(requestsPerMinute: 60, burst: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Assert.False(await bucket.WaitAsync(cts.Token));   // the first call must go through
    }

    [Fact]
    public async Task Cancellation_throws_instead_of_waiting_forever()
    {
        var bucket = new TokenBucket(requestsPerMinute: 0.001, burst: 1);
        await bucket.WaitAsync(CancellationToken.None);    // spend the only token

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bucket.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task Concurrent_callers_are_serialised_not_lost()
    {
        // Every request has to pass through the bucket, so the count of
        // "granted" calls is the count of calls made - none may be skipped.
        var bucket = new TokenBucket(requestsPerMinute: 600_000, burst: 5);
        var granted = 0;

        await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
        {
            await bucket.WaitAsync(CancellationToken.None);
            Interlocked.Increment(ref granted);
        }));

        Assert.Equal(40, granted);
    }
}
