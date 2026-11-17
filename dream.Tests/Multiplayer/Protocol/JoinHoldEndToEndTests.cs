using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Simulation.Runtime;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Multiplayer.Protocol;

// The loading screen's opt-in hold, from the join message through JoinRequestHandler to the runtime.
public sealed class JoinHoldEndToEndTests
{
    private static JoinAcceptedMessage Join(DedicatedServerHost host, string connection, bool? hold)
        => Assert.IsType<JoinAcceptedMessage>(
            host.HandleClientMessage(connection, new ClientJoinRequestMessage("hold-e2e", "Hold E2E", null, hold))
                .Single(reply => reply is JoinAcceptedMessage));

    private static ConnectedPlayer Seat(WorldSessionRuntime runtime, string session)
    {
        Assert.True(runtime.Sessions.TryGet(new PlayerSessionId(session), out ConnectedPlayer? player));
        return player!;
    }

    [Fact]
    public void AnOptedInJoin_IsUntargetedAfterThreeSecondsOfNeutralCommands()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(deferJoinsToTick: false);
        WorldSessionRuntime runtime = host.Runtime!;
        JoinAcceptedMessage accepted = Join(host, "c1", true);
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, accepted.SessionId);
        target.Combatant.Health.SetAuthoritativeState(100_000, 100_000);
        EnemyActor wolf = runtime.Enemies.ActiveActors.First(e => e.IsActive && e.EnemyKind == "Wolf");
        WaveIRuntimeKit.PlaceBeside(wolf, target, new Vector3(1.0f, -0.5f, 0f));
        ulong tick = runtime.CurrentServerTick;
        WaveIRuntimeKit.Tick(runtime, ref tick, PlayerRespawnRules.SpawnProtectionTicks + 60);
        Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
        Assert.NotEqual(0UL, Seat(runtime, accepted.SessionId).ProtectionHeldSinceTick);
    }

    [Fact]
    public void AJoinWithoutTheOptIn_IsNotHeld()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(deferJoinsToTick: false);
        JoinAcceptedMessage accepted = Join(host, "c1", null);
        Assert.Equal(0UL, Seat(host.Runtime!, accepted.SessionId).ProtectionHeldSinceTick);
    }

    [Fact]
    public void ARepeatedOptedInJoin_OnAnAttachedSeat_DoesNotReArmTheHold()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(deferJoinsToTick: false);
        host.TimeProvider = clock;
        JoinAcceptedMessage accepted = Join(host, "c1", true);
        ConnectedPlayer seat = Seat(host.Runtime!, accepted.SessionId);
        Assert.NotEqual(0UL, seat.ProtectionHeldSinceTick);
        seat.ProtectionHeldSinceTick = 0; // released by the first input
        clock.Advance(DedicatedServerHost.RepeatJoinInterval + TimeSpan.FromSeconds(1));
        Join(host, "c1", true);
        Assert.Equal(0UL, seat.ProtectionHeldSinceTick);
    }

    [Fact]
    public void ATakeoverOntoAnAttachedSeat_DoesNotArmTheHold()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(deferJoinsToTick: false);
        host.TimeProvider = clock;
        JoinAcceptedMessage accepted = Join(host, "c1", null);
        ConnectedPlayer seat = Seat(host.Runtime!, accepted.SessionId);
        clock.Advance(DedicatedServerHost.TakeoverSilence + TimeSpan.FromSeconds(1));
        IReadOnlyList<IServerToClientMessage> replies = host.HandleClientMessage(
            "c2", new ClientJoinRequestMessage("hold-e2e", "Hold E2E", null, true));
        Assert.Contains(replies, reply => reply is JoinAcceptedMessage);
        Assert.Equal(0UL, seat.ProtectionHeldSinceTick);
    }
}
