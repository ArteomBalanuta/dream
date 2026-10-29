using System.Text;
using Dream.Multiplayer.Protocol;
using Dream.Server.Worlds;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.Simulation.World;

// Persistence wave B, C0: the world contract types every workstream builds on.
public class WorldDescriptorContractTests
{
    private const string DefaultWireJson =
        "{\"id\":1,\"name\":\"default\",\"width\":56,\"height\":8,\"depth\":56,\"mapSource\":\"generated\",\"generatorKey\":\"default\",\"generatorVersion\":1}";

    [Fact]
    public void Default_IsWorldOne_56x8x56_Generated()
    {
        WorldDescriptor world = WorldDescriptor.Default;

        Assert.Equal(WorldId.Default, world.Id);
        Assert.Equal(1L, world.Id.Value);
        Assert.Equal("default", world.Name);
        Assert.Equal((56, 8, 56), (world.Width, world.Height, world.Depth));
        Assert.Equal(WorldDefaults.StartupFloorSize, world.Width);
        Assert.Equal(WorldMapSource.Generated, world.MapSource);
        Assert.Equal(WorldGeneratorCatalog.DefaultKey, world.GeneratorKey);
        Assert.Equal(WorldGeneratorCatalog.DefaultVersion, world.GeneratorVersion);
        world.Validate();
    }

    [Fact]
    public void WorldId_RejectsZeroAndNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldId(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldId(-1));
        Assert.Equal("7", new WorldId(7).ToString());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(100)]
    public void Validate_AcceptsSquareSizesInRange(int size)
    {
        WorldDescriptor.ForFloorSize(size, new WorldId(2), "w").Validate();
    }

    [Theory]
    [InlineData(4)]
    [InlineData(101)]
    public void Validate_RejectsSizesOutOfRange(int size)
    {
        Assert.Throws<ArgumentException>(() => WorldDescriptor.ForFloorSize(size).Validate());
    }

    [Fact]
    public void Validate_RejectsEveryOtherBound()
    {
        WorldDescriptor ok = WorldDescriptor.Default;

        Assert.Throws<ArgumentException>(() => (ok with { Depth = 20 }).Validate());
        Assert.Throws<ArgumentException>(() => (ok with { Height = 9 }).Validate());
        Assert.Throws<ArgumentException>(() => (ok with { Name = "   " }).Validate());
        Assert.Throws<ArgumentException>(() => (ok with { Name = new string('a', 65) }).Validate());
        Assert.Throws<ArgumentException>(() => (ok with { MapSource = WorldMapSource.Stored }).Validate());
        Assert.Throws<ArgumentException>(() => (ok with { GeneratorKey = "flat" }).Validate());
        Assert.Throws<ArgumentException>(() => (ok with { GeneratorVersion = 2 }).Validate());

        (ok with { Name = "  " + new string('a', 64) + "  " }).Validate();
    }

    [Fact]
    public void MapSourceText_RoundTripsAndRejectsUnknown()
    {
        Assert.Equal("generated", WorldMapSourceText.ToStorage(WorldMapSource.Generated));
        Assert.Equal("stored", WorldMapSourceText.ToStorage(WorldMapSource.Stored));
        Assert.Equal(WorldMapSource.Stored, WorldMapSourceText.Parse("stored"));
        Assert.Throws<InvalidDataException>(() => WorldMapSourceText.Parse("Generated"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SelectorParse_NullOrBlank_IsDefault(string? text)
    {
        WorldSelector selector = WorldSelector.Parse(text);

        Assert.True(selector.IsDefault);
        Assert.Null(selector.Id);
        Assert.Null(selector.Name);
    }

    [Fact]
    public void SelectorParse_DigitsAreAnId_AnythingElseIsATrimmedName()
    {
        WorldSelector byId = WorldSelector.Parse(" 12 ");
        Assert.Equal(new WorldId(12), byId.Id);
        Assert.Null(byId.Name);
        Assert.False(byId.IsDefault);

        WorldSelector byName = WorldSelector.Parse("  my world 2 ");
        Assert.Null(byName.Id);
        Assert.Equal("my world 2", byName.Name);

        Assert.Equal("-3", WorldSelector.Parse("-3").Name);
        Assert.Throws<ArgumentException>(() => WorldSelector.Parse("0"));
        Assert.Throws<ArgumentException>(() => WorldSelector.Parse("99999999999999999999"));
    }

    [Fact]
    public void Wire_DefaultDescriptor_HasThePinnedJsonShape_AndRoundTrips()
    {
        WorldDescriptorWire wire = WorldDescriptorWire.From(WorldDescriptor.Default);
        JoinAcceptedMessage join = new("session-a", 7, 42, InitialWorld: null, World: wire);
        TransportJsonCodec codec = new();
        TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, join.MessageKind, 1);

        byte[] bytes = codec.Encode(envelope, join);
        Assert.Contains(",\"world\":" + DefaultWireJson, Encoding.UTF8.GetString(bytes));

        JoinAcceptedMessage decoded = Assert.IsType<JoinAcceptedMessage>(codec.Decode(bytes).Message);
        Assert.Equal(wire, decoded.World);
        Assert.Equal(WorldDescriptor.Default, decoded.World!.ToDescriptor());
    }

    [Fact]
    public void Wire_NullWorld_IsOmittedFromTheJoin()
    {
        JoinAcceptedMessage join = new("session-a", 7, 42, InitialWorld: null);
        TransportJsonCodec codec = new();
        TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, join.MessageKind, 1);

        Assert.DoesNotContain("\"world\"", Encoding.UTF8.GetString(codec.Encode(envelope, join)));
    }
}
