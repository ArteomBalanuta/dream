using Dream.Client.Replication;
using Dream.Rendering.Effects;
using Dream.Simulation.Content;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Replication;

// A peer's cast arrives as a CastEventDelta and must publish the same SkillCastEvent the local player's own
// cast does. The caster's own event is skipped (it already drew locally), absent and empty lists are "no
// casts", and a malformed event is ignored rather than thrown, because this runs on the frame thread.
public class ClientSnapshotCastApplierTests
{
    private const int LocalActorId = 7;
    private const int PeerActorId = 9;
    private static readonly Vector3 Origin = new(4f, 1f, -2f);

    private static (SceneEventHub Hub, List<SkillCastEvent> Seen) Listen()
    {
        SceneEventHub hub = new();
        List<SkillCastEvent> seen = [];
        hub.Subscribe<SkillCastEvent>(seen.Add);
        return (hub, seen);
    }

    private static ServerSnapshotEnvelope Snapshot(IReadOnlyList<CastEventDelta>? casts)
    {
        return new ServerSnapshotEnvelope(
            1,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>(),
            CastEvents: casts);
    }

    [Theory]
    [InlineData(SkillKind.MeleeArc, SkillFxKind.Arc, 3.5f)]
    [InlineData(SkillKind.RangedBolt, SkillFxKind.Bolt, 18f)]
    [InlineData(SkillKind.SelfHeal, SkillFxKind.Mend, 0f)]
    public void PeerCast_PublishesOneEventWithTheRightKindAndGeometry(SkillKind kind, SkillFxKind fx, float range)
    {
        (SceneEventHub hub, List<SkillCastEvent> seen) = Listen();
        CastEventDelta[] casts = [new(PeerActorId, kind, Origin, new Vector3(3f, 0f, 4f), range)];

        int published = ClientSnapshotCastApplier.Apply(hub, Snapshot(casts), LocalActorId);

        Assert.Equal(1, published);
        SkillCastEvent e = Assert.Single(seen);
        Assert.Equal(fx, e.Kind);
        Assert.Equal(Origin, e.Origin);
        Assert.Equal(range, e.Range);
        Assert.Equal(new Vector3(0.6f, 0f, 0.8f), e.Direction);
        Assert.Equal(PeerActorId, e.CasterActorId);
    }

    [Fact]
    public void OwnCast_IsSuppressed_AndPeerCastInTheSameSnapshotStillDraws()
    {
        (SceneEventHub hub, List<SkillCastEvent> seen) = Listen();
        CastEventDelta[] casts =
        [
            new(LocalActorId, SkillKind.MeleeArc, Origin, Vector3.UnitX, 3.5f),
            new(PeerActorId, SkillKind.RangedBolt, Origin, Vector3.UnitZ, 18f)
        ];

        ClientSnapshotCastApplier.Apply(hub, Snapshot(casts), LocalActorId);

        Assert.Equal(SkillFxKind.Bolt, Assert.Single(seen).Kind);
    }

    [Fact]
    public void UnknownLocalActor_SuppressesNothing()
    {
        (SceneEventHub hub, List<SkillCastEvent> seen) = Listen();
        CastEventDelta[] casts = [new(0, SkillKind.MeleeArc, Origin, Vector3.UnitX, 3.5f)];

        ClientSnapshotCastApplier.Apply(hub, Snapshot(casts), 0);

        Assert.Single(seen);
    }

    [Fact]
    public void AbsentAndEmptyLists_PublishNothingAndDoNotThrow()
    {
        (SceneEventHub hub, List<SkillCastEvent> seen) = Listen();

        Assert.Equal(0, ClientSnapshotCastApplier.Apply(hub, Snapshot(null), LocalActorId));
        Assert.Equal(0, ClientSnapshotCastApplier.Apply(hub, Snapshot(Array.Empty<CastEventDelta>()), LocalActorId));
        Assert.Equal(0, ClientSnapshotCastApplier.Apply(hub, default, LocalActorId));

        Assert.Empty(seen);
    }

    [Fact]
    public void MalformedEvents_AreIgnoredNotThrown_AndGoodOnesAroundThemStillDraw()
    {
        (SceneEventHub hub, List<SkillCastEvent> seen) = Listen();
        CastEventDelta[] casts =
        [
            new(PeerActorId, (SkillKind)99, Origin, Vector3.UnitX, 3.5f),
            new(PeerActorId, SkillKind.MeleeArc, new Vector3(float.NaN, 0f, 0f), Vector3.UnitX, 3.5f),
            new(PeerActorId, SkillKind.MeleeArc, Origin, new Vector3(float.PositiveInfinity, 0f, 0f), 3.5f),
            new(PeerActorId, SkillKind.MeleeArc, Origin, Vector3.UnitX, 0f),
            new(PeerActorId, SkillKind.RangedBolt, Origin, Vector3.UnitX, -1f),
            new(PeerActorId, SkillKind.RangedBolt, Origin, Vector3.UnitX, float.NaN),
            new(PeerActorId, SkillKind.RangedBolt, Origin, Vector3.UnitX, 18f)
        ];

        int published = ClientSnapshotCastApplier.Apply(hub, Snapshot(casts), LocalActorId);

        Assert.Equal(1, published);
        Assert.Equal(SkillFxKind.Bolt, Assert.Single(seen).Kind);
    }

    [Fact]
    public void ABacklog_IsCappedPerSnapshot()
    {
        (SceneEventHub hub, List<SkillCastEvent> seen) = Listen();
        CastEventDelta[] casts = new CastEventDelta[ClientSnapshotCastApplier.MaxCastEventsPerSnapshot + 30];
        Array.Fill(casts, new CastEventDelta(PeerActorId, SkillKind.MeleeArc, Origin, Vector3.UnitX, 3.5f));

        ClientSnapshotCastApplier.Apply(hub, Snapshot(casts), LocalActorId);

        Assert.Equal(ClientSnapshotCastApplier.MaxCastEventsPerSnapshot, seen.Count);
    }

    [Fact]
    public void NullHub_DoesNotThrow()
    {
        CastEventDelta[] casts = [new(PeerActorId, SkillKind.MeleeArc, Origin, Vector3.UnitX, 3.5f)];

        Assert.Equal(0, ClientSnapshotCastApplier.Apply(null!, Snapshot(casts), LocalActorId));
    }
}
