using Xunit;

public class GameWindowTitleBuilderTests
{
    [Fact]
    public void Build_UsesBaseTitleAndFps_WhenNoEndpointIsConnected()
    {
        Assert.Equal("dream | FPS: 144", GameWindowTitleBuilder.Build(144));
    }

    [Fact]
    public void Build_IncludesConnectedEndpoint_WhenAvailable()
    {
        Assert.Equal(
            "dream | 127.0.0.1:5057 | FPS: 64",
            GameWindowTitleBuilder.Build(64, "127.0.0.1:5057"));
    }
}
