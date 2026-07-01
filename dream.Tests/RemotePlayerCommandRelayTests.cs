using OpenTK.Mathematics;
using Xunit;

public class RemotePlayerCommandRelayTests
{
    [Fact]
    public void CreateRelayCommand_SendsFacingButNotRawLookDeltas()
    {
        PlayerCommandFrame localFrame = new(
            new PlayerSessionId("local-session"),
            Sequence: 11,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
            new PlayerMoveIntent(
                ForwardAxis: 1f,
                StrafeAxis: -1f,
                VerticalAxis: 0f,
                JumpPressed: true,
                FreezePressed: false,
                ToggleNoclipPressed: false),
            new PlayerLookIntent(YawDelta: 12f, PitchDelta: -7f),
            PrimaryUsePressed: true);

        PlayerCommandFrame relayFrame = RemotePlayerCommandRelay.CreateRelayCommand(
            localFrame,
            new PlayerSessionId("remote-session"),
            Vector3.UnitX);

        Assert.Equal("remote-session", relayFrame.SessionId.Value);
        Assert.Equal(localFrame.Sequence, relayFrame.Sequence);
        Assert.Equal(localFrame.RequestedMode, relayFrame.RequestedMode);
        Assert.Equal(localFrame.MoveIntent, relayFrame.MoveIntent);
        Assert.Equal(0f, relayFrame.LookIntent.YawDelta);
        Assert.Equal(0f, relayFrame.LookIntent.PitchDelta);
        Assert.Equal(Vector3.UnitX, relayFrame.LookIntent.Facing);
        Assert.True(relayFrame.PrimaryUsePressed);
    }
}
