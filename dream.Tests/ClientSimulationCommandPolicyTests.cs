using Xunit;

public class ClientSimulationCommandPolicyTests
{
    [Fact]
    public void PrepareForLocalSimulation_WhenRunningLocally_ReturnsOriginalCommandFrame()
    {
        PlayerCommandFrame commandFrame = CreateCommandFrame();

        PlayerCommandFrame simulationFrame =
            ClientSimulationCommandPolicy.PrepareForLocalSimulation(commandFrame, useRemoteAuthority: false);

        Assert.Equal(commandFrame.SessionId, simulationFrame.SessionId);
        Assert.Equal(commandFrame.Sequence, simulationFrame.Sequence);
        Assert.Equal(commandFrame.RequestedMode, simulationFrame.RequestedMode);
        Assert.Equal(commandFrame.MoveIntent, simulationFrame.MoveIntent);
        Assert.Equal(PlayerLookIntent.None, simulationFrame.LookIntent);
        Assert.True(simulationFrame.PrimaryUsePressed);
    }

    [Fact]
    public void PrepareForLocalSimulation_WhenUsingRemoteAuthority_PreservesMovementButStripsLookAndPrimaryUse()
    {
        PlayerCommandFrame commandFrame = CreateCommandFrame();

        PlayerCommandFrame simulationFrame =
            ClientSimulationCommandPolicy.PrepareForLocalSimulation(commandFrame, useRemoteAuthority: true);

        Assert.Equal(commandFrame.SessionId, simulationFrame.SessionId);
        Assert.Equal(commandFrame.Sequence, simulationFrame.Sequence);
        Assert.Equal(commandFrame.RequestedMode, simulationFrame.RequestedMode);
        Assert.Equal(commandFrame.MoveIntent, simulationFrame.MoveIntent);
        Assert.Equal(PlayerLookIntent.None, simulationFrame.LookIntent);
        Assert.False(simulationFrame.PrimaryUsePressed);
    }

    private static PlayerCommandFrame CreateCommandFrame()
    {
        return new PlayerCommandFrame(
            new PlayerSessionId("remote-player-1"),
            Sequence: 42,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
            new PlayerMoveIntent(
                ForwardAxis: 1f,
                StrafeAxis: -1f,
                VerticalAxis: 1f,
                JumpPressed: true,
                FreezePressed: true,
                ToggleNoclipPressed: true),
            new PlayerLookIntent(YawDelta: 3.5f, PitchDelta: -2f),
            PrimaryUsePressed: true);
    }
}
