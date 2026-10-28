using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Server;

// NETR-5: the dedicated host buffers commands per session and pops one per tick.
public class BufferedCommandInboxHostTests
{
    private const string Session = "session-a";
    private static readonly PlayerSessionId SessionId = new(Session);

    [Fact]
    public void FramesReceivedBetweenTicks_AreConsumedOnePerTick()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage("connection-a", new ClientJoinRequestMessage(Session, "A"))));
        for (ulong sequence = 1; sequence <= 3; sequence++)
        {
            host.HandleClientMessage("connection-a", new PlayerCommandMessage(Command(sequence)));
        }

        for (ulong expected = 1; expected <= 3; expected++)
        {
            host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
            Assert.Equal(expected, AcceptedSequence(host));
        }

        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        Assert.Equal(3UL, AcceptedSequence(host));
    }

    // Without the release removing the session's ring, a reconnect's fresh sequence would be
    // dropped as stale and the departed connection's frames would be applied instead.
    [Fact]
    public void Release_RemovesTheSessionsBufferedFrames_SoAReconnectStartsFresh()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();
        host.HandleClientMessage("connection-old", new ClientJoinRequestMessage(Session, "A"));
        for (ulong sequence = 40; sequence <= 42; sequence++)
        {
            host.HandleClientMessage("connection-old", new PlayerCommandMessage(Command(sequence)));
        }

        host.ReleaseConnection("connection-old");
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage("connection-new", new ClientJoinRequestMessage(Session, "A"))));
        host.HandleClientMessage("connection-new", new PlayerCommandMessage(Command(1)));

        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        Assert.Equal(1UL, AcceptedSequence(host));
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        Assert.Equal(1UL, AcceptedSequence(host));
    }

    private static ulong? AcceptedSequence(DedicatedServerHost host)
    {
        Assert.True(host.Runtime!.Sessions.TryGet(SessionId, out ConnectedPlayer? player));
        return player!.LastAcceptedCommandSequence;
    }

    private static PlayerCommandFrame Command(ulong sequence)
    {
        return new PlayerCommandFrame(
            SessionId,
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(1f, 0f),
            PlayerLookIntent.None,
            PrimaryUsePressed: false);
    }
}
