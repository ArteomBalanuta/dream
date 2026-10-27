using Dream.Rendering.Core;
using Xunit;

namespace Dream.Tests.Rendering.Core;

public class ResourceCacheClearFaultTests
{
    [Fact]
    public void Clear_ReleasesRemainingItems_WhenOneReleaseThrows_AndRethrowsThatException()
    {
        ResourceCache<string> cache = new();
        cache.GetOrAdd("a", key => key);
        cache.GetOrAdd("b", key => key);
        cache.GetOrAdd("c", key => key);
        List<string> released = [];
        InvalidOperationException failure = new("b failed");

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => cache.Clear(value =>
        {
            released.Add(value);
            if (value == "b")
            {
                throw failure;
            }
        }));

        Assert.Same(failure, thrown);
        Assert.Equal(["a", "b", "c"], released.Order());
        Assert.Empty(cache.Values);
    }

    [Fact]
    public void Clear_ThrowsAggregateException_WhenSeveralReleasesThrow_AfterReleasingEveryItem()
    {
        ResourceCache<string> cache = new();
        cache.GetOrAdd("a", key => key);
        cache.GetOrAdd("b", key => key);
        cache.GetOrAdd("c", key => key);
        List<string> released = [];

        AggregateException thrown = Assert.Throws<AggregateException>(() => cache.Clear(value =>
        {
            released.Add(value);
            if (value != "b")
            {
                throw new InvalidOperationException(value);
            }
        }));

        Assert.Equal(2, thrown.InnerExceptions.Count);
        Assert.Equal(["a", "c"], thrown.InnerExceptions.Select(ex => ex.Message).Order());
        Assert.Equal(["a", "b", "c"], released.Order());
        Assert.Empty(cache.Values);
    }

    [Fact]
    public void DisposeAll_DisposesEveryItem_WhenOneDisposeThrows()
    {
        ResourceCache<TrackedResource> cache = new();
        TrackedResource first = cache.GetOrAdd("first", _ => new TrackedResource(throwOnDispose: true));
        TrackedResource second = cache.GetOrAdd("second", _ => new TrackedResource(throwOnDispose: false));

        Assert.Throws<InvalidOperationException>(() => cache.DisposeAll());

        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.Empty(cache.Values);
    }

    private sealed class TrackedResource(bool throwOnDispose) : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            if (throwOnDispose)
            {
                throw new InvalidOperationException("dispose failed");
            }
        }
    }
}
