using Xunit;
using Dream.Utilities;

namespace Dream.Tests.Utilities;


public class SafeDictionaryLookupTests
{
    [Fact]
    public void TryGetValue_NullKey_ReturnsFalse_WithoutThrowing()
    {
        var source = new Dictionary<object, int>();

        bool found = SafeDictionaryLookup.TryGetValue(source, null, out int value);

        Assert.False(found);
        Assert.Equal(0, value);
    }
}
