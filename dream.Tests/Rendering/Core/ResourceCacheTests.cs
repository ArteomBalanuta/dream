using Xunit;
using Dream.Rendering.Core;

namespace Dream.Tests.Rendering.Core;


public class ResourceCacheTests
{
    [Fact]
    public void GetOrAdd_Returns_Same_Instance_For_Same_Key()
    {
        var cache = new ResourceCache<object>();

        object first = cache.GetOrAdd("grass", _ => new object());
        object second = cache.GetOrAdd("grass", _ => new object());

        Assert.Same(first, second);
    }

    [Fact]
    public void GetOrAdd_Calls_Factory_Only_Once_Per_Key()
    {
        var cache = new ResourceCache<int>();
        int calls = 0;

        int first = cache.GetOrAdd("grass", _ =>
        {
            calls++;
            return 7;
        });

        int second = cache.GetOrAdd("grass", _ =>
        {
            calls++;
            return 9;
        });

        Assert.Equal(7, first);
        Assert.Equal(7, second);
        Assert.Equal(1, calls);
    }
}
