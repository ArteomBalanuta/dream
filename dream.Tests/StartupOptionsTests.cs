using Xunit;

public class StartupOptionsTests
{
    [Fact]
    public void Parse_Defaults_Physics_To_Enabled()
    {
        StartupOptions options = StartupOptions.Parse([]);

        Assert.True(options.PhysicsEnabled);
    }

    [Fact]
    public void Parse_DisablePhysicsFlag_Turns_Physics_Off()
    {
        StartupOptions options = StartupOptions.Parse(["--disable-physics"]);

        Assert.False(options.PhysicsEnabled);
    }
}
