using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Dream.Multiplayer.Protocol;

namespace Dream.Tests.Multiplayer.Protocol;

public sealed class KnownMessageKindsTests
{
    [Fact]
    public void KnownMessageKinds_CoverEveryConcreteProtocolMessageExactlyOnce()
    {
        FieldInfo? field = typeof(TransportJsonCodec).GetField("KnownMessageKinds", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        string[] known = (string[])field!.GetValue(null)!;

        List<string> actual = typeof(IProtocolMessage).Assembly.GetTypes()
            .Where(t => typeof(IProtocolMessage).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .Select(t => ((IProtocolMessage)RuntimeHelpers.GetUninitializedObject(t)).MessageKind)
            .ToList();

        Assert.NotEmpty(actual);
        Assert.Equal(actual.Count, actual.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(known.Length, known.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(actual.OrderBy(k => k, StringComparer.Ordinal), known.OrderBy(k => k, StringComparer.Ordinal));
    }
}
