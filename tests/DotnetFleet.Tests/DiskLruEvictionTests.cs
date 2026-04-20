using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using FluentAssertions;
using NSubstitute;

namespace DotnetFleet.Tests;

/// <summary>
/// Tests for the LRU disk eviction logic extracted to a static helper,
/// mirroring the behaviour in WorkerBackgroundService.ManageDiskSpaceAsync.
/// </summary>
public class DiskLruEvictionTests
{
    // Pure static helper that mirrors the eviction algorithm so it can be
    // unit-tested without standing up a full WorkerBackgroundService.
    private static (List<RepoCache> evicted, long remaining) RunEviction(
        long maxBytes,
        List<RepoCache> caches)
    {
        var ordered = caches.OrderBy(c => c.LastUsedAt).ToList();
        var total = ordered.Sum(c => c.SizeBytes);
        var evicted = new List<RepoCache>();

        while (total > maxBytes && ordered.Count > 0)
        {
            var oldest = ordered[0];
            ordered.RemoveAt(0);
            evicted.Add(oldest);
            total -= oldest.SizeBytes;
        }

        return (evicted, total);
    }

    private static RepoCache MakeCache(string path, long sizeBytes, DateTimeOffset lastUsed) =>
        new() { Id = Guid.NewGuid(), LocalPath = path, SizeBytes = sizeBytes, LastUsedAt = lastUsed };

    [Fact]
    public void No_eviction_when_under_limit()
    {
        var now = DateTimeOffset.UtcNow;
        var caches = new List<RepoCache>
        {
            MakeCache("/repos/a", 100, now.AddHours(-2)),
            MakeCache("/repos/b", 100, now.AddHours(-1)),
        };

        var (evicted, remaining) = RunEviction(maxBytes: 500, caches);

        evicted.Should().BeEmpty();
        remaining.Should().Be(200);
    }

    [Fact]
    public void Oldest_repo_is_evicted_first()
    {
        var now = DateTimeOffset.UtcNow;
        var oldest = MakeCache("/repos/old", 300, now.AddDays(-5));
        var recent = MakeCache("/repos/new", 300, now.AddHours(-1));

        var caches = new List<RepoCache> { recent, oldest }; // unsorted on purpose

        var (evicted, _) = RunEviction(maxBytes: 400, caches);

        evicted.Should().ContainSingle().Which.LocalPath.Should().Be("/repos/old");
    }

    [Fact]
    public void Multiple_repos_evicted_until_under_limit()
    {
        var now = DateTimeOffset.UtcNow;
        var caches = new List<RepoCache>
        {
            MakeCache("/repos/a", 200, now.AddDays(-3)),
            MakeCache("/repos/b", 200, now.AddDays(-2)),
            MakeCache("/repos/c", 200, now.AddDays(-1)),
        };

        var (evicted, remaining) = RunEviction(maxBytes: 250, caches);

        evicted.Should().HaveCount(2);
        remaining.Should().BeLessOrEqualTo(250);
    }

    [Fact]
    public void All_repos_evicted_if_still_over_limit()
    {
        var now = DateTimeOffset.UtcNow;
        var caches = new List<RepoCache>
        {
            MakeCache("/repos/a", 500, now.AddDays(-2)),
            MakeCache("/repos/b", 500, now.AddDays(-1)),
        };

        var (evicted, remaining) = RunEviction(maxBytes: 50, caches);

        evicted.Should().HaveCount(2);
        remaining.Should().Be(0);
    }

    [Fact]
    public void Empty_cache_list_produces_no_evictions()
    {
        var (evicted, remaining) = RunEviction(maxBytes: 1000, []);

        evicted.Should().BeEmpty();
        remaining.Should().Be(0);
    }
}
