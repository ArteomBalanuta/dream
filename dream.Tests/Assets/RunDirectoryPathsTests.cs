using Dream.Assets;
using Xunit;

namespace Dream.Tests.Assets;

// Pure resolvers with an injected environment: these tests never touch process environment variables.
public class RunDirectoryPathsTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("dream-rundir-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    private static Func<string, string?> Env(string? name = null, string? value = null) =>
        key => key == name ? value : null;

    private string SourceTree()
    {
        string root = Path.Combine(_temp, "src");
        Directory.CreateDirectory(Path.Combine(root, "shaders"));
        return root;
    }

    [Fact]
    public void AssetRoot_Unset_UsesBaseDirectory_WithoutLogging()
    {
        List<string> log = new();

        string root = AssetPaths.ResolveRoot(Env(), "base-dir", log.Add);

        Assert.Equal("base-dir", root);
        Assert.Empty(log);
    }

    [Fact]
    public void AssetRoot_Valid_IsUsed_AndLogged()
    {
        string source = SourceTree();
        List<string> log = new();

        string root = AssetPaths.ResolveRoot(Env(AssetPaths.RootEnvironmentVariable, source), "base-dir", log.Add);

        Assert.Equal(Path.GetFullPath(source), root);
        Assert.Equal($"[startup] asset root: {Path.GetFullPath(source)}", Assert.Single(log));
    }

    [Fact]
    public void AssetRoot_MissingDirectory_FallsBackWithOneLine()
    {
        List<string> log = new();

        string root = AssetPaths.ResolveRoot(
            Env(AssetPaths.RootEnvironmentVariable, Path.Combine(_temp, "nope")), "base-dir", log.Add);

        Assert.Equal("base-dir", root);
        Assert.Contains(AssetPaths.RootEnvironmentVariable, Assert.Single(log));
    }

    [Fact]
    public void AssetRoot_WithoutShadersFolder_FallsBackWithOneLine()
    {
        List<string> log = new();

        string root = AssetPaths.ResolveRoot(Env(AssetPaths.RootEnvironmentVariable, _temp), "base-dir", log.Add);

        Assert.Equal("base-dir", root);
        Assert.Contains("shaders", Assert.Single(log));
    }

    [Fact]
    public void DataDir_Unset_IsNull_AndPathsStayUnderBaseConfig()
    {
        Assert.Null(DataPaths.ResolveDirectory(Env(), null));
        Assert.Equal(Path.Combine("base", "config", "player-profiles.db"), DataPaths.DatabasePath(null, "base"));
        Assert.Equal(Path.Combine("base", "config", "desktop-client.txt"), DataPaths.DesktopClientPath(null, "base"));
        Assert.Null(DataPaths.IdentityPath(null));
    }

    [Fact]
    public void DataDir_Set_IsCreated_AndHoldsDatabaseClientConfigAndIdentity()
    {
        string dir = Path.Combine(_temp, "run", "data");
        List<string> log = new();

        string? resolved = DataPaths.ResolveDirectory(Env(DataPaths.EnvironmentVariable, dir), log.Add);

        Assert.Equal(Path.GetFullPath(dir), resolved);
        Assert.True(Directory.Exists(dir));
        Assert.Equal($"[startup] data dir: {resolved}", Assert.Single(log));
        Assert.Equal(Path.Combine(resolved!, "player-profiles.db"), DataPaths.DatabasePath(resolved, "base"));
        Assert.Equal(Path.Combine(resolved!, "desktop-client.txt"), DataPaths.DesktopClientPath(resolved, "base"));
        Assert.Equal(Path.Combine(resolved!, "account-tokens.json"), DataPaths.IdentityPath(resolved));
    }

    [Theory]
    [InlineData("C:\\run:bad")]
    [InlineData("a:b:c")]
    [InlineData("run:data")]
    public void DataDir_WithColonBeyondDriveQualifier_ThrowsNamingTheVariable(string value)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => DataPaths.ResolveDirectory(Env(DataPaths.EnvironmentVariable, value), null));

        Assert.Contains(DataPaths.EnvironmentVariable, ex.Message);
    }

    [Fact]
    public void DataDir_Uncreatable_ThrowsNamingTheVariable()
    {
        string file = Path.Combine(_temp, "a-file");
        File.WriteAllText(file, "x");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => DataPaths.ResolveDirectory(Env(DataPaths.EnvironmentVariable, Path.Combine(file, "sub")), null));

        Assert.Contains(DataPaths.EnvironmentVariable, ex.Message);
    }

    [Fact]
    public void AssetRoot_Set_DoesNotMoveTheDatabase()
    {
        // Synthetic negative: an active asset root with no data dir must leave the database under the base dir.
        string source = SourceTree();
        Func<string, string?> env = Env(AssetPaths.RootEnvironmentVariable, source);

        string root = AssetPaths.ResolveRoot(env, "base-dir", null);
        string? data = DataPaths.ResolveDirectory(env, null);

        Assert.NotEqual("base-dir", root);
        Assert.Equal(Path.Combine("base-dir", "config", "player-profiles.db"), DataPaths.DatabasePath(data, "base-dir"));
    }
}
