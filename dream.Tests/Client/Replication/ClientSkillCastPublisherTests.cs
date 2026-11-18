using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Effects;
using Dream.Simulation.Runtime;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Replication;

// The local player's accepted cast becomes exactly one SkillCastEvent for the FX bridge. A refused cast
// never reaches the publisher (the server sends a rejected result with no AbilityUsedResult), so the
// refusal cases here are the ones the publisher itself owns: unknown key, bad vectors.
public class ClientSkillCastPublisherTests
{
    private static readonly Vector3 Origin = new(4f, 1f, -2f);

    private static (SceneEventHub Hub, List<SkillCastEvent> Seen) Listen()
    {
        SceneEventHub hub = new();
        List<SkillCastEvent> seen = [];
        hub.Subscribe<SkillCastEvent>(seen.Add);
        return (hub, seen);
    }

    [Theory]
    [InlineData("skill.cleave", SkillFxKind.Arc, 3.5f)]
    [InlineData("skill.bolt", SkillFxKind.Bolt, 18f)]
    [InlineData("skill.mend", SkillFxKind.Mend, 0f)]
    public void Accepted_Cast_Publishes_One_Event_Of_The_Right_Kind(string key, SkillFxKind kind, float range)
    {
        (SceneEventHub hub, List<SkillCastEvent> seen) = Listen();

        bool published = ClientSkillCastPublisher.Publish(hub, new AbilityUsedResult(key, 1, 5, 0), Origin, Vector3.UnitX);

        Assert.True(published);
        SkillCastEvent e = Assert.Single(seen);
        Assert.Equal(kind, e.Kind);
        Assert.Equal(range, e.Range);
        Assert.Equal(Origin, e.Origin);
        Assert.Equal(Vector3.UnitX, e.Direction);
    }

    [Fact]
    public void Direction_Is_Flattened_To_A_Horizontal_Unit_Vector()
    {
        (SceneEventHub hub, List<SkillCastEvent> seen) = Listen();

        ClientSkillCastPublisher.Publish(hub, new AbilityUsedResult("skill.bolt", 0, 0, 0), Origin, new Vector3(3f, 7f, 4f));

        Vector3 d = Assert.Single(seen).Direction;
        Assert.Equal(0f, d.Y);
        Assert.Equal(1f, d.Length, 4);
    }

    [Fact]
    public void Zero_Or_Vertical_Direction_Publishes_With_No_Direction()
    {
        (SceneEventHub hub, List<SkillCastEvent> seen) = Listen();

        Assert.True(ClientSkillCastPublisher.Publish(hub, new AbilityUsedResult("skill.cleave", 0, 0, 0), Origin, Vector3.Zero));
        Assert.True(ClientSkillCastPublisher.Publish(hub, new AbilityUsedResult("skill.cleave", 0, 0, 0), Origin, Vector3.UnitY));

        Assert.Equal(2, seen.Count);
        Assert.All(seen, e => Assert.Equal(Vector3.Zero, e.Direction));
    }

    [Theory]
    [InlineData("skill.unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_Or_Missing_Key_Publishes_Nothing_And_Does_Not_Throw(string? key)
    {
        (SceneEventHub hub, List<SkillCastEvent> seen) = Listen();

        bool published = ClientSkillCastPublisher.Publish(hub, new AbilityUsedResult(key!, 0, 0, 0), Origin, Vector3.UnitX);

        Assert.False(published);
        Assert.Empty(seen);
    }

    [Fact]
    public void Non_Finite_Origin_Or_Direction_Publishes_Nothing()
    {
        (SceneEventHub hub, List<SkillCastEvent> seen) = Listen();
        AbilityUsedResult result = new("skill.bolt", 0, 0, 0);

        Assert.False(ClientSkillCastPublisher.Publish(hub, result, new Vector3(float.NaN, 0f, 0f), Vector3.UnitX));
        Assert.False(ClientSkillCastPublisher.Publish(hub, result, Origin, new Vector3(float.PositiveInfinity, 0f, 0f)));
        Assert.Empty(seen);
    }
}
