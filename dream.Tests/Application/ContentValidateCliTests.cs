using Dream.Application;
using Dream.Server;
using Dream.Simulation.Content;
using Xunit;

namespace Dream.Tests.Application;

public sealed class ContentValidateCliTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("validate-content-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        int exit = ContentValidateCli.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    // A copy of the shipped content that a test can break.
    private string CopyShipped()
    {
        string dir = Path.Combine(_temp, "content");
        Directory.CreateDirectory(dir);
        foreach (string file in Directory.GetFiles(ContentCatalog.ShippedDirectory, "*.json"))
        {
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        }

        return dir;
    }

    private static void Replace(string dir, string file, string from, string to)
    {
        string path = Path.Combine(dir, file);
        string text = File.ReadAllText(path);
        Assert.Contains(from, text);
        File.WriteAllText(path, text.Replace(from, to));
    }

    [Fact]
    public void ShippedContent_ExitsZeroAndPrintsTheSummary()
    {
        (int exit, string output, string err) = Run();

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, err);
        Assert.Contains("ok: digest=" + ContentCatalog.Shipped.ContentDigest, output);
        Assert.Contains($"items={ContentCatalog.Shipped.Items.Count} ", output);
        Assert.Matches(@"\(\d+ ms\)", output);
    }

    [Fact]
    public void LootReferencingAMissingItem_ExitsEightNamingLootJson()
    {
        string dir = CopyShipped();
        Replace(dir, "loot.json", "\"gear.leather-cap\"", "\"gear.no-such-item\"");

        (int exit, string output, string err) = Run("--dir", dir);

        Assert.Equal(ServerBootstrap.ContentInvalidExitCode, exit);
        Assert.Contains("loot.json", err);
        Assert.DoesNotContain("ok:", output);
    }

    [Fact]
    public void MissingSkillIcon_WarnsAndExitsZero_ButStrictExitsEight()
    {
        string dir = CopyShipped();
        Replace(dir, "skills.json", "\"iconKey\": \"skill.cleave\"", "\"iconKey\": \"skill.no-such-icon\"");

        (int exit, string output, _) = Run("--dir", dir);
        Assert.Equal(0, exit);
        Assert.Contains("warning: skill 'skill.cleave' has no sprite for iconKey 'skill.no-such-icon'", output);

        (int strictExit, string strictOutput, _) = Run("--dir", dir, "--strict");
        Assert.Equal(ServerBootstrap.ContentInvalidExitCode, strictExit);
        Assert.Contains("warning:", strictOutput);
    }

    [Fact]
    public void UnreadableUiDirectory_WarnsInsteadOfFailing()
    {
        (int exit, string output, _) = Run("--ui", Path.Combine(_temp, "no-ui"));

        Assert.Equal(0, exit);
        Assert.Contains("warning: ui sprites not checked", output);
    }

    [Theory]
    [InlineData("--dir", "--strict")]
    [InlineData("--ui", "--strict")]
    [InlineData("--dir", "--ui")]
    public void OptionValueStartingWithDashes_IsAUsageError_NotAPath(string option, string next)
    {
        (int exit, string output, string err) = Run(option, next);

        Assert.Equal(1, exit);
        Assert.Contains("needs a value", err);
        Assert.Contains("usage:", err);
        Assert.Equal(string.Empty, output);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--dir")]
    [InlineData("extra")]
    public void UnknownFlagOrMissingValue_ExitsOneWithUsage(string arg)
    {
        (int exit, string output, string err) = Run(arg);

        Assert.Equal(1, exit);
        Assert.Contains("usage:", err);
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void TrySelectProcessMode_MapsTheFlag()
    {
        Assert.True(Program.TrySelectProcessMode(["--validate-content", "--strict"], out ProcessMode mode, out _, out _));
        Assert.Equal(ProcessMode.ValidateContent, mode);

        Assert.False(Program.TrySelectProcessMode(["--validate-contents"], out _, out _, out string? message));
        Assert.Contains("--validate-content", message);
    }
}
