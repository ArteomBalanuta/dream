using Xunit;

public class PlayerMotorTests
{
    [Fact]
    public void GetNoclipSpeed_Is_Faster_Than_Standard_Move_Speed()
    {
        GameSettings settings = new(new OpenTK.Mathematics.Vector2i(1440, 1024))
        {
            MoveSpeed = 4f,
            NoclipSpeed = 18f
        };

        float noclipSpeed = PlayerMotor.GetNoclipSpeed(settings);

        Assert.True(noclipSpeed > settings.MoveSpeed);
    }
}
