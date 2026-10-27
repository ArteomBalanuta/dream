using Xunit;
using Dream.Rendering.Actors;

namespace Dream.Tests.Rendering.Actors;


public class EnemyVisualStateCacheTests
{
    [Fact]
    public void GetOrAdd_ReturnsSameInstanceForSameId()
    {
        EnemyVisualStateCache cache = new();

        EnemyVisualState first = cache.GetOrAdd(2000);
        EnemyVisualState second = cache.GetOrAdd(2000);

        Assert.Same(first, second);
        Assert.NotSame(first, cache.GetOrAdd(2001));
    }

    [Fact]
    public void PruneExcept_RemovesDespawnedIdsAndKeepsCurrent()
    {
        EnemyVisualStateCache cache = new();
        EnemyVisualState kept = cache.GetOrAdd(1);
        cache.GetOrAdd(2);

        cache.PruneExcept(new HashSet<int> { 1 });

        Assert.Equal(1, cache.Count);
        Assert.Same(kept, cache.GetOrAdd(1));
    }

    [Fact]
    public void GetOrAdd_AfterPrune_ReturnsFreshState()
    {
        EnemyVisualStateCache cache = new();
        EnemyVisualState old = cache.GetOrAdd(7);

        cache.PruneExcept(new HashSet<int>());
        EnemyVisualState respawned = cache.GetOrAdd(7);

        Assert.NotSame(old, respawned);
    }
}
