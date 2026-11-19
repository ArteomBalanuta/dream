using Dream.Assets;
using Dream.Client.Identity;
using Xunit;

namespace Dream.Tests.Client.Identity;

public class ClientIdentityDataDirTests
{
    private const string PerUserRoot = "appdata";
    private const string DataDirFile = "data-dir/account-tokens.json";

    private static Func<string, string?> Env(string? identityFile) =>
        key => key == ClientIdentityStore.PathEnvironmentVariable ? identityFile : null;

    [Fact]
    public void NeitherSet_UsesPerUserDefault()
    {
        Assert.Equal(
            Path.Combine(PerUserRoot, "Dream", "account-tokens.json"),
            ClientIdentityStore.ResolvePath(Env(null), null, PerUserRoot));
    }

    [Fact]
    public void IdentityFileEnvironment_NeverResolvesTheDataDir()
    {
        string path = ClientIdentityStore.ResolvePathLazy(
            Env("explicit.json"),
            () => throw new InvalidOperationException("bad DREAM_DATA_DIR"),
            PerUserRoot);

        Assert.Equal("explicit.json", path);
    }

    [Fact]
    public void NoIdentityFileEnvironment_ResolvesTheDataDirLazilyAndPropagatesItsError()
    {
        Assert.Throws<InvalidOperationException>(() => ClientIdentityStore.ResolvePathLazy(
            Env(null),
            () => throw new InvalidOperationException("bad DREAM_DATA_DIR"),
            PerUserRoot));
    }

    [Fact]
    public void DataDirOnly_UsesTheDataDirFile()
    {
        Assert.Equal(DataDirFile, ClientIdentityStore.ResolvePath(Env(null), DataDirFile, PerUserRoot));
    }

    [Fact]
    public void IdentityFileEnvironment_BeatsTheDataDir()
    {
        Assert.Equal("explicit.json", ClientIdentityStore.ResolvePath(Env("explicit.json"), DataDirFile, PerUserRoot));
    }

    [Fact]
    public void BlankIdentityFileEnvironment_DoesNotShadowTheDataDir()
    {
        Assert.Equal(DataDirFile, ClientIdentityStore.ResolvePath(Env("  "), DataDirFile, PerUserRoot));
    }

    [Fact]
    public void DataPathsIdentity_FeedsTheResolver_EndToEnd()
    {
        string dir = Directory.CreateTempSubdirectory("dream-ident-").FullName;
        try
        {
            string? resolved = DataPaths.ResolveDirectory(
                key => key == DataPaths.EnvironmentVariable ? dir : null, null);

            string path = ClientIdentityStore.ResolvePath(Env(null), DataPaths.IdentityPath(resolved), PerUserRoot);

            Assert.Equal(Path.Combine(Path.GetFullPath(dir), "account-tokens.json"), path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
