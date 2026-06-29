using Xunit;

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
                ToggleNoclipPressed: false),
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
}
