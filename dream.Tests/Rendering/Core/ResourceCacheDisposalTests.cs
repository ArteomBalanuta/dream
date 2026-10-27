using System;
using System.IO;
using Dream.Rendering.Core;
using Xunit;

namespace Dream.Tests.Rendering.Core;

public class ResourceCacheDisposalTests
{
    [Fact]
    public void DisposeAll_DisposesEachValueExactlyOnce()
    {
        ResourceCache<FakeDisposable> cache = new();
        FakeDisposable a = cache.GetOrAdd("a", _ => new FakeDisposable());
        FakeDisposable b = cache.GetOrAdd("b", _ => new FakeDisposable());

        cache.DisposeAll();

        Assert.Equal(1, a.DisposeCount);
        Assert.Equal(1, b.DisposeCount);
        Assert.Empty(cache.Values);
    }

    [Fact]
    public void DisposeAll_SecondCallIsNoOp()
    {
        ResourceCache<FakeDisposable> cache = new();
        FakeDisposable a = cache.GetOrAdd("a", _ => new FakeDisposable());

        cache.DisposeAll();
        cache.DisposeAll();

        Assert.Equal(1, a.DisposeCount);
    }

    [Fact]
    public void GetOrAdd_AfterClear_InvokesFactoryAgain()
    {
        ResourceCache<FakeDisposable> cache = new();
        int factoryCalls = 0;
        FakeDisposable Create(string _)
        {
            factoryCalls++;
            return new FakeDisposable();
        }

        FakeDisposable first = cache.GetOrAdd("a", Create);
        cache.Clear();
        FakeDisposable second = cache.GetOrAdd("a", Create);

        Assert.Equal(2, factoryCalls);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Clear_WithRelease_InvokesReleaseOncePerValue()
    {
        ResourceCache<FakeDisposable> cache = new();
        cache.GetOrAdd("a", _ => new FakeDisposable());
        cache.GetOrAdd("b", _ => new FakeDisposable());
        int released = 0;

        cache.Clear(_ => released++);
        cache.Clear(_ => released++);

        Assert.Equal(2, released);
    }

    [Theory]
    [InlineData("dream/Rendering/Models/AnimatedModel.cs")]
    [InlineData("dream/Client/Startup/StartupMenuRenderer.cs")]
    public void Dispose_IsGuardedByDisposedField(string relativePath)
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath));

        Assert.Contains("private bool _disposed", source);
        int disposeIndex = source.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(disposeIndex >= 0);
        string body = source.Substring(disposeIndex, 200);
        Assert.Contains("if (_disposed)", body);
    }

    private sealed class FakeDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
