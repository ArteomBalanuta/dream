using Dream.Rendering.Core;
using Xunit;

namespace Dream.Tests.Rendering.Core;

public class DisposableRegistryTests
{
    [Fact]
    public void Replace_Disposes_Previous_Value_Exactly_Once_And_Never_The_New_One()
    {
        DisposableRegistry<string, FakeDisposable> registry = new();
        FakeDisposable first = new();
        FakeDisposable second = new();

        registry.Replace("default", first);
        registry.Replace("default", second);

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, second.DisposeCount);
        Assert.Same(second, registry["default"]);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Replace_With_Same_Instance_Does_Not_Dispose_It()
    {
        DisposableRegistry<string, FakeDisposable> registry = new();
        FakeDisposable value = new();

        registry.Replace("default", value);
        registry.Replace("default", value);

        Assert.Equal(0, value.DisposeCount);
        Assert.Same(value, registry["default"]);
    }

    [Fact]
    public void Replace_Stores_New_Value_Even_When_Previous_Dispose_Throws()
    {
        DisposableRegistry<string, FakeDisposable> registry = new();
        FakeDisposable first = new() { ThrowOnDispose = true };
        FakeDisposable second = new();
        registry.Replace("default", first);

        Assert.Throws<InvalidOperationException>(() => registry.Replace("default", second));

        Assert.Same(second, registry["default"]);
    }

    [Fact]
    public void TryGet_And_Indexer_Return_Stored_Value_And_Unknown_Key_Throws()
    {
        DisposableRegistry<string, FakeDisposable> registry = new();
        FakeDisposable value = new();
        registry.Replace("cube", value);

        Assert.True(registry.TryGet("cube", out FakeDisposable? found));
        Assert.Same(value, found);
        Assert.False(registry.TryGet("missing", out _));
        Assert.Throws<KeyNotFoundException>(() => registry["missing"]);
    }

    [Fact]
    public void DisposeAll_Disposes_Every_Value_Once_And_Is_Idempotent()
    {
        DisposableRegistry<string, FakeDisposable> registry = new();
        FakeDisposable a = new();
        FakeDisposable b = new();
        registry.Replace("a", a);
        registry.Replace("b", b);

        registry.DisposeAll();
        registry.DisposeAll();

        Assert.Equal(1, a.DisposeCount);
        Assert.Equal(1, b.DisposeCount);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void DisposeAll_Keeps_Disposing_After_One_Throws_And_Rethrows_It()
    {
        DisposableRegistry<string, FakeDisposable> registry = new();
        FakeDisposable before = new();
        FakeDisposable failing = new() { ThrowOnDispose = true };
        FakeDisposable after = new();
        registry.Replace("before", before);
        registry.Replace("failing", failing);
        registry.Replace("after", after);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(registry.DisposeAll);

        Assert.Equal(FakeDisposable.FailureMessage, thrown.Message);
        Assert.Equal(1, before.DisposeCount);
        Assert.Equal(1, failing.DisposeCount);
        Assert.Equal(1, after.DisposeCount);
        Assert.Equal(0, registry.Count);

        registry.DisposeAll();
        Assert.Equal(1, failing.DisposeCount);
    }

    [Fact]
    public void DisposeAll_Aggregates_Several_Failures()
    {
        DisposableRegistry<string, FakeDisposable> registry = new();
        FakeDisposable first = new() { ThrowOnDispose = true };
        FakeDisposable middle = new();
        FakeDisposable last = new() { ThrowOnDispose = true };
        registry.Replace("first", first);
        registry.Replace("middle", middle);
        registry.Replace("last", last);

        AggregateException thrown = Assert.Throws<AggregateException>(registry.DisposeAll);

        Assert.Equal(2, thrown.InnerExceptions.Count);
        Assert.All(thrown.InnerExceptions, ex => Assert.IsType<InvalidOperationException>(ex));
        Assert.Equal(1, middle.DisposeCount);
    }

    // Mirrors Render.LoadShader: the new value is built before Replace, so a factory that throws
    // (a failed shader compile) never reaches the registry and the old value stays usable.
    [Fact]
    public void Factory_That_Throws_Before_Replace_Leaves_Old_Value_Intact()
    {
        DisposableRegistry<string, FakeDisposable> registry = new();
        FakeDisposable original = new();
        registry.Replace("default", original);

        Assert.Throws<InvalidOperationException>(() => LoadInto(registry, "default", ThrowingFactory));

        Assert.Same(original, registry["default"]);
        Assert.Equal(0, original.DisposeCount);
    }

    private static void LoadInto(
        DisposableRegistry<string, FakeDisposable> registry,
        string key,
        Func<FakeDisposable> factory)
    {
        FakeDisposable created = factory();
        registry.Replace(key, created);
    }

    private static FakeDisposable ThrowingFactory()
    {
        throw new InvalidOperationException("compile failed");
    }

    private sealed class FakeDisposable : IDisposable
    {
        public const string FailureMessage = "dispose failed";

        public int DisposeCount { get; private set; }

        public bool ThrowOnDispose { get; init; }

        public void Dispose()
        {
            DisposeCount++;
            if (ThrowOnDispose)
            {
                throw new InvalidOperationException(FailureMessage);
            }
        }
    }
}
