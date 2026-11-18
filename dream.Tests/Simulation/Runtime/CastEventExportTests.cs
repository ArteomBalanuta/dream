using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Content;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using OpenTK.Mathematics;

namespace Dream.Tests.Simulation.Runtime;

// Wave 2 remote cast visibility, server side: an accepted skill cast is exported once on the snapshot
// so peers can draw it. Presentation only; a refused cast tells nobody anything.
public sealed class CastEventExportTests
{
    private const string Mend = "skill.mend";
    private static readonly PlayerSessionId A = new("cast-export-a");

    private static WorldSessionRuntime Joined(out IAuthoritativePlayerTarget target)
    {
        WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, "Alice");
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        target = WaveIRuntimeKit.Target(runtime, A.Value);
        return runtime;
    }

    private static AuthoritativeActionResult CastMend(WorldSessionRuntime runtime, ulong sequence)
        => runtime.ProcessAuthoritativeAction(new PlayerActionMessage(A, sequence, new SkillUseIntent(Mend)));

    private static CastEventDelta Cast(int actor, float x) => new(actor, SkillKind.RangedBolt, new Vector3(x, 0, 0), Vector3.UnitZ, 18f);

    private static ServerSnapshotEnvelope Snapshot(ulong tick, params CastEventDelta[] casts) => new(
        tick,
        new Dictionary<string, ulong>(),
        [],
        PlayerInteractionMode.Combat,
        null,
        [],
        [],
        CastEvents: casts.Length == 0 ? null : casts);

    [Fact]
    public void AnAcceptedCast_ExportsExactlyOneEvent_WithItsKindOriginDirectionAndRange()
    {
        using WorldSessionRuntime runtime = Joined(out IAuthoritativePlayerTarget target);
        Assert.True(ContentCatalog.Shipped.TryGetSkill(Mend, out SkillDefinition? mend));
        target.Combatant.Health.ApplyDamage(mend!.HealAmount + 10);

        Assert.True(CastMend(runtime, 1).Accepted);
        ServerSnapshotEnvelope snapshot = runtime.ExportServerSnapshotEnvelope();

        CastEventDelta cast = Assert.Single(snapshot.CastEvents!);
        Assert.Equal(target.Combatant.Actor.Id, cast.ActorId);
        Assert.Equal(SkillKind.SelfHeal, cast.Kind);
        Assert.Equal(mend.Range, cast.Range);
        Assert.True(float.IsFinite(cast.Origin.X) && float.IsFinite(cast.Origin.Y) && float.IsFinite(cast.Origin.Z));
        Assert.True(float.IsFinite(cast.Direction.X) && float.IsFinite(cast.Direction.Y) && float.IsFinite(cast.Direction.Z));
    }

    [Fact]
    public void AnAimOverridesOnlyTheDirection()
    {
        using WorldSessionRuntime runtime = Joined(out IAuthoritativePlayerTarget target);
        target.Combatant.Health.ApplyDamage(50);
        Vector3 aim = new(0f, 0f, -2f);

        Assert.True(runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(A, 1, new SkillUseIntent(Mend, aim))).Accepted);
        CastEventDelta cast = Assert.Single(runtime.ExportServerSnapshotEnvelope().CastEvents!);

        Assert.Equal(aim, cast.Direction);
    }

    [Fact]
    public void TheListIsClearedByTheExport_SoACastIsNeverSentTwice()
    {
        using WorldSessionRuntime runtime = Joined(out IAuthoritativePlayerTarget target);
        target.Combatant.Health.ApplyDamage(50);
        Assert.True(CastMend(runtime, 1).Accepted);

        Assert.NotNull(runtime.ExportServerSnapshotEnvelope().CastEvents);
        Assert.Null(runtime.ExportServerSnapshotEnvelope().CastEvents);
    }

    [Fact]
    public void ATickWithNoCast_ExportsNullNotAnEmptyList()
    {
        using WorldSessionRuntime runtime = Joined(out _);

        Assert.Null(runtime.ExportServerSnapshotEnvelope().CastEvents);
    }

    [Fact]
    public void AMendAtFullHealth_IsRefused_AndExportsNothing()
    {
        using WorldSessionRuntime runtime = Joined(out _);

        Assert.False(CastMend(runtime, 1).Accepted);

        Assert.Null(runtime.ExportServerSnapshotEnvelope().CastEvents);
    }

    [Fact]
    public void ACastOnCooldown_IsRefused_AndOnlyTheFirstIsExported()
    {
        using WorldSessionRuntime runtime = Joined(out IAuthoritativePlayerTarget target);
        target.Combatant.Health.ApplyDamage(80);
        Assert.True(CastMend(runtime, 1).Accepted);
        target.Combatant.Health.ApplyDamage(80);

        Assert.False(CastMend(runtime, 2).Accepted);

        Assert.Single(runtime.ExportServerSnapshotEnvelope().CastEvents!);
    }

    [Fact]
    public void ACastWithoutEnoughMana_IsRefused_AndExportsNothing()
    {
        using WorldSessionRuntime runtime = Joined(out IAuthoritativePlayerTarget target);
        target.Combatant.Health.ApplyDamage(80);
        while (target.Combatant.TrySpendMana(1))
        {
        }

        Assert.False(CastMend(runtime, 1).Accepted);

        Assert.Null(runtime.ExportServerSnapshotEnvelope().CastEvents);
    }

    [Fact]
    public void AnUnknownSkill_ExportsNothing()
    {
        using WorldSessionRuntime runtime = Joined(out _);

        Assert.False(runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(A, 1, new SkillUseIntent("skill.nope"))).Accepted);

        Assert.Null(runtime.ExportServerSnapshotEnvelope().CastEvents);
    }

    [Fact]
    public void TheQueueIsBoundedPerTick()
    {
        using WorldSessionRuntime runtime = Joined(out _);

        for (int i = 0; i < 1000; i++)
        {
            runtime.QueueCastEvent(Cast(1, i));
        }

        Assert.Equal(256, runtime.ExportServerSnapshotEnvelope().CastEvents!.Count);
    }

    [Fact]
    public void ACastDoesNotMoveTheWorldChecksumOrTheMutationSequence()
    {
        using WorldSessionRuntime with = Joined(out _);
        using WorldSessionRuntime without = Joined(out _);
        with.QueueCastEvent(Cast(1, 0));

        ServerSnapshotEnvelope a = with.ExportServerSnapshotEnvelope();
        ServerSnapshotEnvelope b = without.ExportServerSnapshotEnvelope();

        Assert.Equal(b.WorldChecksum, a.WorldChecksum);
        Assert.Equal(b.WorldChecksumSequence, a.WorldChecksumSequence);
        Assert.Equal(without.LastWorldMutationSequence, with.LastWorldMutationSequence);
    }

    [Fact]
    public void Coalescing_KeepsBothTicksCasts_InOrder()
    {
        ServerSnapshotCoalesceBuffer buffer = new();
        buffer.Begin(Snapshot(10, Cast(1, 1), Cast(1, 2)));
        buffer.Append(Snapshot(11, Cast(2, 3)));

        IReadOnlyList<CastEventDelta> merged = buffer.ToEnvelope().CastEvents!;

        Assert.Equal([1f, 2f, 3f], merged.Select(c => c.Origin.X));
        Assert.Equal(3, buffer.CastEventCount);
    }

    [Fact]
    public void Coalescing_WithNoCasts_ExportsNull()
    {
        ServerSnapshotCoalesceBuffer buffer = new();
        buffer.Begin(Snapshot(10));
        buffer.Append(Snapshot(11));

        Assert.Null(buffer.ToEnvelope().CastEvents);
    }

    [Fact]
    public void Coalescing_TruncatesSilentlyAtTheCap_KeepingTheOldest()
    {
        ServerSnapshotCoalesceBuffer buffer = new();
        CastEventDelta[] batch = Enumerable.Range(0, 400).Select(i => Cast(1, i)).ToArray();
        buffer.Begin(Snapshot(10, batch));
        for (ulong t = 11; t < 16; t++)
        {
            buffer.Append(Snapshot(t, batch));
        }

        ServerSnapshotEnvelope merged = buffer.ToEnvelope();

        Assert.Equal(1024, ServerSnapshotCoalesceBuffer.MaxCoalescedCastEvents);
        Assert.Equal(ServerSnapshotCoalesceBuffer.MaxCoalescedCastEvents, merged.CastEvents!.Count);
        Assert.Equal(0f, merged.CastEvents[0].Origin.X);
        // Cosmetic: no deltas are claimed, so no resync is owed for the lost casts.
        Assert.False(ServerConnection.HasDeltas(merged));
    }

    [Fact]
    public void DropDeltas_AndReset_ClearTheCasts()
    {
        ServerSnapshotCoalesceBuffer buffer = new();
        buffer.Begin(Snapshot(10, Cast(1, 1)));
        buffer.Append(Snapshot(11, Cast(1, 2)));

        buffer.DropDeltas();
        Assert.Null(buffer.ToEnvelope().CastEvents);
        Assert.Equal(0, buffer.CastEventCount);

        buffer.Begin(Snapshot(12, Cast(1, 3)));
        buffer.Reset();
        buffer.Begin(Snapshot(13));
        Assert.Null(buffer.ToEnvelope().CastEvents);
    }

    [Fact]
    public void WithoutDeltas_ClearsTheCasts_ButHasDeltasIgnoresThem()
    {
        ServerSnapshotEnvelope snapshot = Snapshot(10, Cast(1, 1));

        Assert.False(ServerConnection.HasDeltas(snapshot));
        Assert.Null(ServerConnection.WithoutDeltas(new ServerSnapshotMessage(snapshot)).Snapshot.CastEvents);
    }
}
