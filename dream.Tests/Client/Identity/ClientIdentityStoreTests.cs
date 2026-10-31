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
    public void AFileHeldByAnotherProcess_ReportsInUse_NotACheckTheFolderError()
    {
        string path = NewPath();
        ClientIdentityStore store = new(path) { MutexWait = TimeSpan.FromMilliseconds(200) };
        using ManualResetEventSlim held = new(false);
        using ManualResetEventSlim release = new(false);
        // A mutex belongs to the thread that took it, so another thread plays the other window.
        Thread holder = new(() =>
        {
            using Mutex mutex = new(false, ClientIdentityStore.MutexNameFor(Path.GetFullPath(path)));
            mutex.WaitOne();
            held.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            mutex.ReleaseMutex();
        });
        holder.Start();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));

            IOException error = Assert.ThrowsAny<IOException>(() => store.GetOrCreateToken("play.example.com:5057"));

            Assert.Contains("in use by another process", error.Message);
            Assert.DoesNotContain("Check the folder", error.Message);
        }
        finally
        {
            release.Set();
            holder.Join();
        }
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
    public void FileOfANewerVersion_IsRefusedClearly_AndLeftWhereItIs()
    {
        string path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string Text = "{\"version\":2,\"servers\":{\"local\":\"x\"}}";
        File.WriteAllText(path, Text);

        IOException error = Assert.ThrowsAny<IOException>(() => new ClientIdentityStore(path).GetOrCreateToken("local"));

        Assert.Contains("newer version", error.Message);
        Assert.Equal(Text, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "account-tokens.json.bad-*"));
    }

    [Fact]
    public void FileOfAnOlderUnknownVersion_IsMovedAside_NotOverwritten()
    {
        string path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"version\":0,\"servers\":{\"local\":\"x\"}}");

        new ClientIdentityStore(path).GetOrCreateToken("local");

        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "account-tokens.json.bad-*"));
    }

    [Theory]
    [InlineData("Play.Example.COM.", "play.example.com")]
    [InlineData("[::1]", "::1")]
    [InlineData("[2001:DB8::1]", "2001:db8::1")]
    [InlineData("2001:0db8:0:0:0:0:0:1", "2001:db8::1")]
    public void ServerKey_CanonicalizesTheHost(string typed, string expected)
    {
        Assert.Equal($"{expected}:5057", ClientIdentityStore.ServerKeyFor(typed, 5057, isLocalServer: false));
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
