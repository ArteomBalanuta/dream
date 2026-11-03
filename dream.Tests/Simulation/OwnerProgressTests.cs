using Xunit;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Simulation.Runtime;
using OpenTK.Mathematics;

namespace Dream.Tests.Simulation;

// M8. Owner-only progress, below the network: what the wire projection hides and keeps, that the checksum
// still sees the truth, and when the owner is sent its own values. The loopback proof is in
// M8EndToEndLoopbackTests.
public sealed class OwnerProgressTests
{
    private static readonly PlayerSimulationTuning Tuning = PlayerSimulationTuning.AuthoritativeDefault;

    private static RuntimeAuthoritativeSessionHost HostWith(bool ownerOnly, params string[] sessions)
    {
        RuntimeAuthoritativeSessionHost host = RuntimeAuthoritativeSessionHost.CreateDefault();
        host.Runtime.OwnerOnlyProgress = ownerOnly;
        foreach (string session in sessions)
        {
            host.JoinSession(session, "Name " + session);
        }

        return host;
    }

    private static void SetProgress(WorldSessionRuntime runtime, string session, int level, int experience, int strength)
    {
        PlayerCombatant combatant = Combatant(runtime, session);
        combatant.ApplyProgression(level, experience);
        combatant.ApplyBaseStats(new PlayerCombatStats { Strength = strength, AttackSpeed = combatant.BaseStats.AttackSpeed, Vitality = combatant.BaseStats.Vitality });
    }

    private static PlayerCombatant Combatant(WorldSessionRuntime runtime, string session)
    {
        return WaveIRuntimeKit.Target(runtime, session).Combatant;
    }

    private static ulong Run(RuntimeAuthoritativeSessionHost host, ulong fromTick, int ticks)
    {
        ulong tick = fromTick;
        for (int i = 0; i < ticks; i++)
        {
            host.AdvanceTick(++tick, SimulationClock.FixedDeltaSeconds, [], Tuning);
        }

        return tick;
    }

    private static List<ServerPlayerProgress> Progress(WorldSessionRuntime runtime, string session)
    {
        List<SessionServerMessage> drained = [];
        runtime.DrainSessionMessagesInto(drained);
        return [.. drained.Where(m => m.SessionId.Value == session).Select(m => m.Message).OfType<ServerPlayerProgress>()];
    }

    [Fact]
    public void TheWireProjection_HidesExperienceAndStats_AndKeepsTheLevel()
    {
        using RuntimeAuthoritativeSessionHost host = HostWith(true, "alpha", "bravo");
        SetProgress(host.Runtime, "alpha", 4, 912347, 41);

        ActorSnapshot truth = host.Runtime.CaptureActorSnapshots().Single(a => a.SessionId == "alpha");
        ActorSnapshot wire = host.Runtime.CaptureWireActorSnapshots().Single(a => a.SessionId == "alpha");

        Assert.Equal(912347, truth.Progression!.Value.Experience);
        Assert.Equal(41, truth.CombatStats!.Value.Strength);
        Assert.Null(wire.CombatStats);
        Assert.Equal(4, wire.Progression!.Value.Level);
        Assert.Equal(WorldSessionRuntime.HiddenExperience, wire.Progression.Value.Experience);
        Assert.Equal(WorldSessionRuntime.HiddenExperience, wire.Progression.Value.ExperienceToNextLevel);
    }

    [Fact]
    public void TheJoinAcceptedActors_AreProjectedToo()
    {
        using RuntimeAuthoritativeSessionHost host = HostWith(true, "alpha");
        SetProgress(host.Runtime, "alpha", 4, 912347, 41);

        JoinAcceptedMessage accepted = host.JoinSession("bravo", "Name bravo");

        ActorSnapshot alpha = accepted.EffectiveInitialActors.Single(a => a.SessionId == "alpha");
        Assert.Null(alpha.CombatStats);
        Assert.Equal(WorldSessionRuntime.HiddenExperience, alpha.Progression!.Value.Experience);
        Assert.Equal(4, alpha.Progression.Value.Level);
    }

    [Fact]
    public void TheSnapshotAndTheResync_AreProjected()
    {
        using RuntimeAuthoritativeSessionHost host = HostWith(true, "alpha");
        SetProgress(host.Runtime, "alpha", 4, 912347, 41);

        foreach (ServerSnapshotEnvelope envelope in new[] { host.ExportSnapshot(), host.ExportWorldResync() })
        {
            ActorSnapshot alpha = envelope.Actors.Single(a => a.SessionId == "alpha");
            Assert.Null(alpha.CombatStats);
            Assert.Equal(WorldSessionRuntime.HiddenExperience, alpha.Progression!.Value.Experience);
        }
    }

    [Fact]
    public void WithTheFlagOff_TheWireIsTheTruth()
    {
        using RuntimeAuthoritativeSessionHost host = HostWith(false, "alpha");
        SetProgress(host.Runtime, "alpha", 4, 912347, 41);

        Assert.Equal(
            host.Runtime.CaptureActorSnapshots(),
            host.Runtime.CaptureWireActorSnapshots());
    }

    [Fact]
    public void TheChecksum_IsTheSame_WhateverTheFlagSays()
    {
        using RuntimeAuthoritativeSessionHost host = HostWith(false, "alpha", "bravo");
        SetProgress(host.Runtime, "alpha", 4, 912347, 41);
        ulong off = SimulationStateChecksum.Compute(host.Runtime);

        host.Runtime.OwnerOnlyProgress = true;
        ulong on = SimulationStateChecksum.Compute(host.Runtime);

        Assert.Equal(off, on);
        // And it still depends on the hidden values: changing one changes the checksum.
        SetProgress(host.Runtime, "alpha", 4, 912348, 41);
        Assert.NotEqual(on, SimulationStateChecksum.Compute(host.Runtime));
    }

    [Fact]
    public void EnemiesAreNotTouchedByTheProjection()
    {
        using RuntimeAuthoritativeSessionHost host = HostWith(true, "alpha");

        IReadOnlyList<ActorSnapshot> truth = host.Runtime.CaptureActorSnapshots();
        IReadOnlyList<ActorSnapshot> wire = host.Runtime.CaptureWireActorSnapshots();

        Assert.Equal(truth.Where(a => a.Kind != CreatureKind.Player), wire.Where(a => a.Kind != CreatureKind.Player));
    }

    [Fact]
    public void TheOwnerGetsItsValues_OnTheVitalsCadence_ThenOnlyWhenTheyChange()
    {
        using RuntimeAuthoritativeSessionHost host = HostWith(true, "alpha", "bravo");
        SetProgress(host.Runtime, "alpha", 4, 77, 9);

        ulong tick = Run(host, 0, 16);
        List<SessionServerMessage> first = [];
        host.Runtime.DrainSessionMessagesInto(first);

        // Each session got its own first message; alpha's carries the real values.
        ServerPlayerProgress alpha = Assert.Single(first, m => m.SessionId.Value == "alpha" && m.Message is ServerPlayerProgress).Message as ServerPlayerProgress ?? throw new InvalidOperationException();
        Assert.Equal(4, alpha.Level);
        Assert.Equal(77, alpha.Experience);
        Assert.Equal(9, alpha.BaseStats.Strength);
        Assert.True(alpha.ExperienceToNextLevel > 0);
        Assert.Single(first, m => m.SessionId.Value == "bravo" && m.Message is ServerPlayerProgress);

        // Nothing changed: nothing sent, however many cadence ticks pass.
        tick = Run(host, tick, 48);
        Assert.Empty(Progress(host.Runtime, "alpha"));

        // A change is sent at the next cadence tick and not before.
        SetProgress(host.Runtime, "alpha", 4, 78, 9);
        tick = Run(host, tick, 1);
        Assert.Empty(Progress(host.Runtime, "alpha"));
        Run(host, tick, 16);
        ServerPlayerProgress changed = Assert.Single(Progress(host.Runtime, "alpha"));
        Assert.Equal(78, changed.Experience);
    }

    [Fact]
    public void WithTheFlagOff_NoProgressIsSent()
    {
        using RuntimeAuthoritativeSessionHost host = HostWith(false, "alpha");

        Run(host, 0, 40);

        Assert.Empty(Progress(host.Runtime, "alpha"));
    }

    [Fact]
    public void ALeavingSession_IsForgotten_SoARejoinIsSentItsValuesAgain()
    {
        using RuntimeAuthoritativeSessionHost host = HostWith(true, "alpha");
        ulong tick = Run(host, 0, 16);
        Assert.Single(Progress(host.Runtime, "alpha"));

        host.ReleaseSession("alpha");
        host.JoinSession("alpha", "Name alpha");
        Run(host, tick, 16);

        Assert.Single(Progress(host.Runtime, "alpha"));
    }

    [Fact]
    public void TheClient_KeepsItsValues_WhenAnActorCarriesHiddenExperience_AndTakesTheOwnersMessage()
    {
        using ClientWorldSession world = new(56, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        ClientCorrectionApplier.ApplyOwnerProgress(world, new ServerPlayerProgress(4, 77, 500, new PlayerCombatStatsSnapshot(9, 1f, 6)));
        Assert.Equal(4, world.Player.Combatant.Level);
        Assert.Equal(77, world.Player.Combatant.Experience);
        Assert.Equal(9, world.Player.Combatant.BaseStats.Strength);

        // The public actor of the same player: a level and hidden values. They must not overwrite.
        ActorSnapshot actor = new(
            1,
            CreatureKind.Player,
            "alpha",
            new Vector3(5f, 1.5f, 5f),
            Vector3.UnitZ,
            100,
            100,
            true,
            Progression: new PlayerProgressionSnapshot(4, WorldSessionRuntime.HiddenExperience, WorldSessionRuntime.HiddenExperience));
        ServerSnapshotEnvelope envelope = new(1, new Dictionary<string, ulong>(), [actor], PlayerInteractionMode.Combat, null, [], []);
        ClientCorrectionApplier.ApplySnapshotToRuntime(world, envelope, "alpha");

        Assert.Equal(77, world.Player.Combatant.Experience);
        Assert.Equal(4, world.Player.Combatant.Level);
        Assert.Equal(9, world.Player.Combatant.BaseStats.Strength);
        Assert.Equal(500, world.CapturePresentationState(64f).HudStatus.ExperienceToNextLevel);
    }
}
