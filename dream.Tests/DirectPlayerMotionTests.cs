using OpenTK.Mathematics;
using Xunit;

public class DirectPlayerMotionTests
{
    [Fact]
    public void Update_WhenGameplayInputAllowed_Moves_In_WorldDirection()
    {
        var motion = new DirectPlayerMotion();
        var settings = new GameSettings(new Vector2i(1440, 1024));

        Vector3 next = motion.Update(
            currentPosition: new Vector3(5f, 1.5f, 5f),
            command: new MovementCommand(Vector3.UnitX, false, false),
            settings,
            deltaTime: 0.5f,
            allowGameplayInput: true);

        Assert.Equal(new Vector3(8f, 1.5f, 5f), next);
    }

    [Fact]
    public void Update_WhenFrozen_Does_Not_Move()
    {
        var motion = new DirectPlayerMotion();
        var settings = new GameSettings(new Vector2i(1440, 1024));

        _ = motion.Update(new Vector3(5f, 1.5f, 5f), new MovementCommand(Vector3.Zero, false, true), settings, 0.016f, true);
        Vector3 next = motion.Update(new Vector3(5f, 1.5f, 5f), new MovementCommand(Vector3.UnitZ, false, false), settings, 1f, true);

        Assert.Equal(new Vector3(5f, 1.5f, 5f), next);
        Assert.True(motion.IsFrozen);
    }
}
