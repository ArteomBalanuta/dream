using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Tests.Multiplayer.Sessions;


public class LatestPlayerCommandInboxTests
{
    [Fact]
    public void Drain_ReturnsLatestPendingCommandPerRequestedSession()
    {
        LatestPlayerCommandInbox inbox = new();
        inbox.Enqueue(new PlayerCommandFrame(
            new PlayerSessionId("session-a"),
            Sequence: 1,
            PlayerModeSwitchIntent.None,
            PlayerMoveIntent.None,
            PlayerLookIntent.None,
            PrimaryUsePressed: false));
        inbox.Enqueue(new PlayerCommandFrame(
            new PlayerSessionId("session-a"),
            Sequence: 3,
            PlayerModeSwitchIntent.None,
            PlayerMoveIntent.None,
            PlayerLookIntent.None,
            PrimaryUsePressed: false));
        inbox.Enqueue(new PlayerCommandFrame(
            new PlayerSessionId("session-b"),
            Sequence: 2,
            PlayerModeSwitchIntent.None,
            PlayerMoveIntent.None,
            PlayerLookIntent.None,
            PrimaryUsePressed: false));

        IReadOnlyList<PlayerCommandFrame> drained = inbox.Drain(
            [new PlayerSessionId("session-a"), new PlayerSessionId("session-b")]);

        Assert.Collection(
            drained.OrderBy(frame => frame.SessionId.Value, StringComparer.Ordinal),
            first =>
            {
                Assert.Equal("session-a", first.SessionId.Value);
                Assert.Equal((ulong)3, first.Sequence);
            },
            second =>
            {
                Assert.Equal("session-b", second.SessionId.Value);
                Assert.Equal((ulong)2, second.Sequence);
            });
    }

    [Fact]
    public void Enqueue_AccumulatesLookDeltaAndPreservesOneShotInputsUntilDrain()
    {
        LatestPlayerCommandInbox inbox = new();
        inbox.Enqueue(new PlayerCommandFrame(
            new PlayerSessionId("session-a"),
            Sequence: 1,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
            new PlayerMoveIntent(
                ForwardAxis: 1f,
                StrafeAxis: 0f,
                VerticalAxis: 0f,
                JumpPressed: true,
                FreezePressed: false,
                ToggleNoclipPressed: false,
                SprintHeld: true),
            new PlayerLookIntent(YawDelta: 0.25f, PitchDelta: -0.10f),
            PrimaryUsePressed: true));
        inbox.Enqueue(new PlayerCommandFrame(
            new PlayerSessionId("session-a"),
            Sequence: 2,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(
                ForwardAxis: 1f,
                StrafeAxis: -1f,
                VerticalAxis: 0f,
                JumpPressed: false,
                FreezePressed: true,
                ToggleNoclipPressed: true),
            new PlayerLookIntent(YawDelta: 0.35f, PitchDelta: 0.05f),
            PrimaryUsePressed: false));

        PlayerCommandFrame drained = Assert.Single(inbox.Drain([new PlayerSessionId("session-a")]));

        Assert.Equal((ulong)2, drained.Sequence);
        Assert.Equal(PlayerInteractionMode.Building, drained.RequestedMode.Mode);
        Assert.Equal(1f, drained.MoveIntent.ForwardAxis);
        Assert.Equal(-1f, drained.MoveIntent.StrafeAxis);
        Assert.True(drained.MoveIntent.JumpPressed);
        Assert.True(drained.MoveIntent.FreezePressed);
        Assert.True(drained.MoveIntent.ToggleNoclipPressed);
        // Sprint is HELD state, not a press: the newer frame's value wins, as
        // ClientOutboundCommandQueue.Merge does. The newer frame here has it released.
        Assert.False(drained.MoveIntent.SprintHeld);
        Assert.Equal(0.60f, drained.LookIntent.YawDelta, 5);
        Assert.Equal(-0.05f, drained.LookIntent.PitchDelta, 5);
        Assert.True(drained.PrimaryUsePressed);
    }

    [Fact]
    public void Enqueue_PreservesOneFrameNoclipToggleUntilDrain()
    {
        LatestPlayerCommandInbox inbox = new();
        PlayerSessionId sessionId = new("session-a");

        inbox.Enqueue(new PlayerCommandFrame(
            sessionId,
            Sequence: 1,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(
                ForwardAxis: 1f,
                StrafeAxis: 0f,
                VerticalAxis: 0f,
                JumpPressed: false,
                FreezePressed: false,
                ToggleNoclipPressed: true),
            PlayerLookIntent.None,
            PrimaryUsePressed: false));
        inbox.Enqueue(new PlayerCommandFrame(
            sessionId,
            Sequence: 2,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(
                ForwardAxis: 1f,
                StrafeAxis: 0f,
                VerticalAxis: 0f,
                JumpPressed: false,
                FreezePressed: false,
                ToggleNoclipPressed: false),
            PlayerLookIntent.None,
            PrimaryUsePressed: false));

        PlayerCommandFrame drained = Assert.Single(inbox.Drain([sessionId]));

        Assert.Equal((ulong)2, drained.Sequence);
        Assert.True(drained.MoveIntent.ToggleNoclipPressed);
    }

    [Fact]
    public void Enqueue_IgnoresOlderPendingCommandForSameSession()
    {
        LatestPlayerCommandInbox inbox = new();
        PlayerSessionId sessionId = new("session-a");
        inbox.Enqueue(new PlayerCommandFrame(
            sessionId,
            Sequence: 10,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(ForwardAxis: 1f, StrafeAxis: 0f),
            PlayerLookIntent.None,
            PrimaryUsePressed: false));

        inbox.Enqueue(new PlayerCommandFrame(
            sessionId,
            Sequence: 9,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(ForwardAxis: -1f, StrafeAxis: 0f),
            PlayerLookIntent.None,
            PrimaryUsePressed: true));

        PlayerCommandFrame drained = Assert.Single(inbox.Drain([sessionId]));

        Assert.Equal((ulong)10, drained.Sequence);
        Assert.Equal(1f, drained.MoveIntent.ForwardAxis);
        Assert.False(drained.PrimaryUsePressed);
    }

    [Fact]
    public async Task EnqueueAndDrain_CanRunConcurrentlyAcrossServerReceiveAndTickLoops()
    {
        LatestPlayerCommandInbox inbox = new();
        PlayerSessionId[] sessionIds = Enumerable
            .Range(0, 8)
            .Select(index => new PlayerSessionId($"session-{index}"))
            .ToArray();

        Task[] producers = Enumerable
            .Range(0, 8)
            .Select(worker => Task.Run(() =>
            {
                for (int sequence = 0; sequence < 5_000; sequence++)
                {
                    PlayerSessionId sessionId = sessionIds[(worker + sequence) % sessionIds.Length];
                    inbox.Enqueue(CreateFrame(sessionId, (ulong)sequence));
                }
            }))
            .ToArray();
        Task consumer = Task.Run(() =>
        {
            for (int iteration = 0; iteration < 5_000; iteration++)
            {
                _ = inbox.Drain(sessionIds);
            }
        });

        await Task.WhenAll(producers.Append(consumer));
    }

    private static PlayerCommandFrame CreateFrame(PlayerSessionId sessionId, ulong sequence)
    {
        return new PlayerCommandFrame(
            sessionId,
            sequence,
            PlayerModeSwitchIntent.None,
            PlayerMoveIntent.None,
            PlayerLookIntent.None,
            PrimaryUsePressed: false);
    }
}
