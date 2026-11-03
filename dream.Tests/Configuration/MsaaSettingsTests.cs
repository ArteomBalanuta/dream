using System.IO;
using Xunit;
using Dream.Client.Menu;
using Dream.Configuration;
using Dream.Application;
using Dream.Rendering.Ui.Menu;
using OpenTK.Mathematics;

namespace Dream.Tests.Configuration;

public class MsaaSettingsTests
{
    [Fact]
    public void Default_Is_4x()
    {
        Assert.Equal(4, new DesktopClientConfig().MsaaSamples);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(-3, 0)]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    [InlineData(8, 4)]
    [InlineData(100, 4)]
    public void Sanitize_Snaps_To_Allowed_Set(int input, int expected)
    {
        Assert.Equal(expected, MsaaSettings.Sanitize(input));
        DesktopClientConfig config = new() { MsaaSamples = input };
        config.Sanitize();
        Assert.Equal(expected, config.MsaaSamples);
    }

    [Theory]
    [InlineData(4, "0", 0)]
    [InlineData(0, "4", 4)]
    [InlineData(4, " 2 ", 2)]
    [InlineData(4, "16", 4)]
    [InlineData(2, "fast", 2)]
    [InlineData(2, "", 2)]
    [InlineData(2, null, 2)]
    public void Resolve_Prefers_Environment_Override(int config, string? env, int expected)
    {
        Assert.Equal(expected, MsaaSettings.Resolve(config, env));
    }

    [Fact]
    public void Describe_Labels_Off_And_Counts()
    {
        Assert.Equal("off", MsaaSettings.Describe(0));
        Assert.Equal("4x", MsaaSettings.Describe(4));
    }

    [Fact]
    public void LoadOrCreate_Writes_Default_Msaa_When_Missing()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "desktop-client.txt");

        DesktopClientConfig config = DesktopClientTextConfig.LoadOrCreate(path);

        Assert.Equal(4, config.MsaaSamples);
        Assert.Contains("msaa=4", File.ReadAllLines(path));
    }

    [Theory]
    [InlineData("msaa=0", 0)]
    [InlineData("msaa=2", 2)]
    [InlineData("msaa=8", 4)]
    [InlineData("msaa=-1", 0)]
    [InlineData("msaa=lots", 4)]
    [InlineData("msaa=", 4)]
    public void LoadOrCreate_Reads_And_Sanitizes_Msaa(string line, int expected)
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "desktop-client.txt");
        File.WriteAllText(path, line + "\n");

        Assert.Equal(expected, DesktopClientTextConfig.LoadOrCreate(path).MsaaSamples);
    }

    [Fact]
    public void Save_Then_Load_Round_Trips_Msaa()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "desktop-client.txt");
        DesktopClientTextConfig.Save(path, new DesktopClientConfig { MsaaSamples = 2 });

        Assert.Equal(2, DesktopClientTextConfig.LoadOrCreate(path).MsaaSamples);
    }

    [Fact]
    public void MenuRow_Steps_0_2_4_And_Persists_Without_Applying()
    {
        GameSettings settings = new(new Vector2i(1440, 1024));
        DesktopClientConfig config = new();
        int applied = 0;
        int persisted = 0;
        List<MenuOptionRow> rows = MenuOptionRows.CreateStandard(settings, config, () => applied++, () => persisted++);
        MenuOptionRow msaa = rows.Single(r => r.Label.StartsWith("Anti-Aliasing", StringComparison.Ordinal));

        Assert.Equal(2f, msaa.GetValue!());
        Assert.Equal("4x", msaa.FormatValue(msaa.GetValue!()));
        msaa.SetValue!(0f);
        Assert.Equal(0, config.MsaaSamples);
        Assert.Equal("off", msaa.FormatValue(0f));
        msaa.SetValue!(1f);
        Assert.Equal(2, config.MsaaSamples);
        msaa.Commit!();
        Assert.Equal(1, persisted);
        Assert.Equal(0, applied);
    }
}
