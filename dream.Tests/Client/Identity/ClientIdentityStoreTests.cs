using Dream.Client.Identity;
using Dream.Simulation.Characters;
using Xunit;

namespace Dream.Tests.Client.Identity;

public class ClientIdentityStoreTests
{
    private static string NewPath()
    {
        return Path.Combine(Directory.CreateTempSubdirectory().FullName, "Dream", "account-tokens.json");
    }

    [Fact]
    public void Token_Is43ValidCharacters_AndStableAcrossInstances()
    {
        string path = NewPath();

        string first = new ClientIdentityStore(path).GetOrCreateToken("play.example.com:5057");
        string again = new ClientIdentityStore(path).GetOrCreateToken("play.example.com:5057");

        Assert.Equal(AccountKey.TokenChars, first.Length);
        Assert.True(AccountKey.TryFromToken(first, out _));
        Assert.Equal(first, again);
    }

    [Fact]
    public void DifferentServers_GetDifferentTokens()
    {
        ClientIdentityStore store = new(NewPath());

        Assert.NotEqual(store.GetOrCreateToken("a.example.com:5057"), store.GetOrCreateToken("b.example.com:5057"));
        Assert.NotEqual(store.GetOrCreateToken("localhost:5057"), store.GetOrCreateToken("127.0.0.1:5057"));
    }

    [Fact]
    public void ServerKey_IsLocalForTheIntegratedServer_WhateverThePort()
    {
        Assert.Equal("local", ClientIdentityStore.ServerKeyFor("127.0.0.1", 6123, isLocalServer: true));
        Assert.Equal("local", ClientIdentityStore.ServerKeyFor("localhost", 7777, isLocalServer: true));
        Assert.Equal("play.example.com:5057", ClientIdentityStore.ServerKeyFor(" Play.Example.COM ", 5057, isLocalServer: false));
    }

    [Fact]
    public void CorruptFile_MovesAside_IsNotOverwritten_AndANewStoreStarts()
    {
        string path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        string token = new ClientIdentityStore(path).GetOrCreateToken("local");

        string[] aside = Directory.GetFiles(Path.GetDirectoryName(path)!, "account-tokens.json.bad-*");
        string asideFile = Assert.Single(aside);
        Assert.Equal("{ this is not json", File.ReadAllText(asideFile));
        Assert.True(AccountKey.TryFromToken(token, out _));
        Assert.Equal(token, new ClientIdentityStore(path).GetOrCreateToken("local"));
        Assert.DoesNotContain(token, asideFile);
    }

    [Fact]
    public void FileOfAnotherVersion_IsMovedAside_NotOverwritten()
    {
        string path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"version\":2,\"servers\":{\"local\":\"x\"}}");

        new ClientIdentityStore(path).GetOrCreateToken("local");

        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "account-tokens.json.bad-*"));
    }

    [Fact]
    public void ConcurrentStores_LoseNoEntry()
    {
        string path = NewPath();
        string[] keys = Enumerable.Range(0, 24).Select(i => $"server-{i}.example.com:5057").ToArray();

        Parallel.ForEach(keys, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            key => new ClientIdentityStore(path).GetOrCreateToken(key));

        ClientIdentityStore verify = new(path);
        string text = File.ReadAllText(path);
        foreach (string key in keys)
        {
            string token = verify.GetOrCreateToken(key);
            Assert.Contains(token, text);
        }

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void UnixFile_IsMode0600()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows relies on the per-user directory's inherited ACL; there is no mode to assert.
            return;
        }

        string path = NewPath();
        new ClientIdentityStore(path).GetOrCreateToken("local");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void UnwritableLocation_ThrowsAReadableMessage_WithoutAnyToken()
    {
        string blocker = Path.Combine(Directory.CreateTempSubdirectory().FullName, "file");
        File.WriteAllText(blocker, "x");
        ClientIdentityStore store = new(Path.Combine(blocker, "sub", "account-tokens.json"));

        IOException ex = Assert.Throws<IOException>(() => store.GetOrCreateToken("local"));

        Assert.Contains("player identity", ex.Message);
        Assert.Contains(ClientIdentityStore.PathEnvironmentVariable, ex.Message);
    }

    [Fact]
    public void ExistingMalformedEntry_IsReplacedWithAValidToken()
    {
        string path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"version\":1,\"servers\":{\"local\":\"short\"}}");

        string token = new ClientIdentityStore(path).GetOrCreateToken("local");

        Assert.True(AccountKey.TryFromToken(token, out _));
    }
}
