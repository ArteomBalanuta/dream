using System.IO;
using Xunit;
using Dream.Configuration;

namespace Dream.Tests.Configuration;

public class FrameRateCapTests
{
    [Fact]
    public void DesktopClientConfig_Defaults_To_144_Fps()
    {
        DesktopClientConfig config = new();

        Assert.Equal(144, config.MaxFramesPerSecond);
        Assert.Equal(FrameRateCap.Default, config.MaxFramesPerSecond);
    }

    [Theory]
    [InlineData(10, 30)]
    [InlineData(1, 30)]
    [InlineData(30, 30)]
    [InlineData(144, 144)]
    [InlineData(500, 500)]
    [InlineData(1000, 500)]
    [InlineData(0, 0)]
    [InlineData(-1, 30)]
    [InlineData(-500, 30)]
    public void Sanitize_Clamps_To_Range_And_Keeps_Zero_As_Uncapped(int input, int expected)
    {
        DesktopClientConfig config = new() { MaxFramesPerSecond = input };

        config.Sanitize();

        Assert.Equal(expected, config.MaxFramesPerSecond);
        Assert.Equal(expected, FrameRateCap.Sanitize(input));
    }

    [Fact]
    public void LoadOrCreate_Writes_Default_Max_Fps_When_Missing()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(tempDir, "desktop-client.txt");

        DesktopClientConfig config = DesktopClientTextConfig.LoadOrCreate(path);

        Assert.Equal(144, config.MaxFramesPerSecond);
        Assert.Contains("max_fps=144", File.ReadAllLines(path));
    }

    [Theory]
    [InlineData("max_fps=90", 90)]
    [InlineData("max_fps=0", 0)]
    [InlineData("max_fps=10", 30)]
    [InlineData("max_fps=1000", 500)]
    [InlineData("max_fps=-5", 30)]
    [InlineData("max_fps=120.4", 120)]
    [InlineData("max_fps=fast", 144)]
    [InlineData("max_fps=", 144)]
    public void LoadOrCreate_Parses_And_Clamps_Max_Fps(string line, int expected)
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(tempDir, "desktop-client.txt");
        File.WriteAllText(path, string.Join('\n', new[] { "host=127.0.0.1", line }));

        DesktopClientConfig config = DesktopClientTextConfig.LoadOrCreate(path);

        Assert.Equal(expected, config.MaxFramesPerSecond);
    }

    [Fact]
    public void Max_Fps_Round_Trips_Through_Text_Config()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(tempDir, "desktop-client.txt");

        DesktopClientTextConfig.Save(path, new DesktopClientConfig { MaxFramesPerSecond = 75 });
        DesktopClientConfig reloaded = DesktopClientTextConfig.LoadOrCreate(path);
        reloaded.PlayerName = "changed";
        DesktopClientTextConfig.Save(path, reloaded);
        DesktopClientConfig reloadedAgain = DesktopClientTextConfig.LoadOrCreate(path);

        Assert.Equal(75, reloaded.MaxFramesPerSecond);
        Assert.Equal(75, reloadedAgain.MaxFramesPerSecond);
        Assert.Contains("max_fps=75", File.ReadAllLines(path));
    }

    [Fact]
    public void Uncapped_Max_Fps_Round_Trips_Through_Text_Config()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(tempDir, "desktop-client.txt");

        DesktopClientTextConfig.Save(path, new DesktopClientConfig { MaxFramesPerSecond = 0 });

        Assert.Equal(0, DesktopClientTextConfig.LoadOrCreate(path).MaxFramesPerSecond);
    }

    [Theory]
    [InlineData(144, "60", 60)]
    [InlineData(144, " 240 ", 240)]
    [InlineData(144, "0", 0)]
    [InlineData(0, "120", 120)]
    [InlineData(144, "10", 30)]
    [InlineData(144, "1000", 500)]
    [InlineData(144, "-20", 30)]
    public void Resolve_Environment_Override_Wins_And_Is_Sanitized(int configValue, string environmentValue, int expected)
    {
        Assert.Equal(expected, FrameRateCap.Resolve(configValue, environmentValue));
    }

    [Theory]
    [InlineData(144, "fast", 144)]
    [InlineData(90, "60.5", 90)]
    [InlineData(0, "abc", 0)]
    [InlineData(144, "", 144)]
    [InlineData(144, "   ", 144)]
    [InlineData(144, null, 144)]
    [InlineData(75, null, 75)]
    [InlineData(10, null, 30)]
    [InlineData(0, null, 0)]
    public void Resolve_Falls_Back_To_Config_When_Environment_Is_Missing_Or_Invalid(
        int configValue,
        string? environmentValue,
        int expected)
    {
        Assert.Equal(expected, FrameRateCap.Resolve(configValue, environmentValue));
    }

    [Fact]
    public void Describe_Reports_Uncapped_Or_Fps()
    {
        Assert.Equal("uncapped", FrameRateCap.Describe(0));
        Assert.Equal("144 FPS", FrameRateCap.Describe(144));
    }
}
