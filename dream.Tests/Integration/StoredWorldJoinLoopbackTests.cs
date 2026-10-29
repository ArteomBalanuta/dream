using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.World;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Integration;

// Persistence wave E, workstream D: joining a stored world over real TCP (the server writes the
// terrain transfer, the client builds its map from it), and the guard that a generated world's join
// is unchanged by wave E.
public sealed partial class StoredWorldJoinLoopbackTests(ITestOutputHelper output)
{
    // Measured at wave E C0 (3e30c12, protocol v8) before A, B or C merged: a client joins the
    // generated 56x8x56 world of StoredWorldDb(stored: false) (seed 20261029) and the server ticks
    // three more times with a snapshot per tick; the pinned frames are the join's (the snapshot frames
    // are checked to arrive but not byte-compared, see CaptureGeneratedJoinAsync). Generated worlds send no terrain manifest and no
    // terrain frames, so every frame must stay byte-identical (plan rule 4, decision E7).
    private const int C0FrameCount = 3;
    private const long C0TotalBytes = 8_492;
    private const string C0FrameKinds =
        "server.hello,server.join-accepted,server.world-chunk";
    private const string C0Sha256 = "40391109031e35d04b26c1232579691b99ac37bd1d68166b003232bed4b42a42";

    [Fact]
    public async Task GeneratedWorldJoin_FrameSequenceAndBytes_EqualTheC0Capture()
    {
        FrameCapture first = await CaptureGeneratedJoinAsync();
        FrameCapture second = await CaptureGeneratedJoinAsync();
        output.WriteLine($"generated join: {first.Kinds.Split(',').Length} frames, {first.TotalBytes:N0} bytes, kinds {first.Kinds}, sha256 {first.Sha256}");
        // The capture is deterministic run to run.
        Assert.True(first == second, DescribeDifference(first, second));
        Assert.DoesNotContain("terrain", first.Kinds, StringComparison.Ordinal);
        Assert.Equal(C0Sha256, first.Sha256);
        Assert.Equal(C0FrameKinds, first.Kinds);
        Assert.Equal(C0TotalBytes, first.TotalBytes);
        Assert.Equal(C0FrameCount, first.Kinds.Split(',').Length);
    }

    // Over real TCP a client joins a stored World10k: checksum, raised key sets and rubble equal.
    [Fact]
    public async Task Client_JoiningAStoredWorld10k_BuildsTheServersMap()
    {
        using StoredWorldDb db = new(100);
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(db.CreateHost());
        Assert.IsType<Dream.Simulation.World.Terrain.StoredMapSource>(server.Runtime.Map.Source);
        await using WireLoopbackClient client = await server.ConnectAndJoinAsync("stored-joiner");
        using ClientWorldSession world = new(server.Runtime.Map.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        TerrainPersistenceLoopbackTests.ApplyAllFrames(client, world);
        AssertClientMatches(server.Runtime.Map, world.Map);
        Assert.Equal(WorldMapSource.Stored, world.World.MapSource);
        await server.DisconnectAsync(client);
    }

    // Terrain destroyed (cube and sculpture cells among them), restart, join again: equal again.
    [Fact]
    public async Task Client_JoinsAgain_AfterTerrainWasDestroyedAndTheServerRestarted()
    {
        using StoredWorldDb db = new();
        for (int restart = 0; restart < 2; restart++)
        {
            using (SoakServer script = new(db.CreateHost()))
            {
                for (int tick = 0; tick < 1500; tick++)
                {
                    script.Step(tick);
                }

                Assert.True(script.Fractures > 0);
            }

            SqliteConnection.ClearAllPools();
            await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(db.CreateHost());
            Assert.NotEmpty(server.Runtime.Map.RubbleCells);
            await using WireLoopbackClient client = await server.ConnectAndJoinAsync($"stored-rejoiner-{restart}");
            using ClientWorldSession world = new(server.Runtime.Map.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            TerrainPersistenceLoopbackTests.ApplyAllFrames(client, world);
            AssertClientMatches(server.Runtime.Map, world.Map);
            output.WriteLine($"restart {restart + 1}: {server.Runtime.Map.RubbleCells.Count} rubble cells, client matches");
            await server.DisconnectAsync(client);
        }
    }

    // Two clients connect before either has applied its transfer; both build the same map.
    [Fact]
    public async Task TwoClients_JoinAtOnce_BothBuildTheServersMap()
    {
        using StoredWorldDb db = new(100);
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(db.CreateHost());
        await using WireLoopbackClient first = await server.ConnectAndJoinAsync("stored-a");
        await using WireLoopbackClient second = await server.ConnectAndJoinAsync("stored-b");
        foreach (WireLoopbackClient client in new[] { first, second })
        {
            using ClientWorldSession world = new(server.Runtime.Map.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
            TerrainPersistenceLoopbackTests.ApplyAllFrames(client, world);
            AssertClientMatches(server.Runtime.Map, world.Map);
        }

        await server.DisconnectAsync(first);
        await server.DisconnectAsync(second);
    }

    private static void AssertClientMatches(Map server, Map client)
    {
        Assert.Equal(WorldStateChecksum.Compute(server), WorldStateChecksum.Compute(client));
        Assert.Equal(
            server.GeometryCells.Keys.Where(key => key.Y > 0).ToHashSet(),
            client.GeometryCells.Keys.Where(key => key.Y > 0).ToHashSet());
        Assert.Equal(server.RubbleCells.Keys.ToHashSet(), client.RubbleCells.Keys.ToHashSet());
        Assert.Empty(client.FindInvariantViolations());
    }

    private static async Task<FrameCapture> CaptureGeneratedJoinAsync()
    {
        using StoredWorldDb db = new(stored: false);
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(db.CreateHost());
        await using WireLoopbackClient client = await server.ConnectAndJoinAsync("capture-client", awaitPrefetchBeforeTicking: true);
        for (int i = 0; i < 3; i++)
        {
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }

        // The snapshots that follow carry the scripted wolf's position, which depends on how many ticks
        // the loopback harness executed by then (its clock may need a nudge on a busy machine). The
        // join itself (hello, join-accepted, the world chunks) does not.
        List<WireFrame> frames = [.. client.Frames.Where(frame => frame.Decoded.Envelope.MessageKind != "server.snapshot")];
        Assert.NotEmpty(client.Snapshots());
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        List<string> texts = [];
        foreach (WireFrame frame in frames)
        {
            // The server tick a join lands on depends on whether the harness clock needed its nudge
            // tick (currentServerTick is 0 or 1); everything else is fixed. Normalise the tick.
            string text = TickNumber().Replace(System.Text.Encoding.UTF8.GetString(frame.Payload), "\"$1\":0");
            texts.Add(text);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
            total += bytes.Length;
        }

        string kinds = string.Join(",", frames.Select(frame => frame.Decoded.Envelope.MessageKind));
        return new FrameCapture(kinds, total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant())
        {
            Texts = [.. texts]
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"""(currentServerTick|serverTick)"":\d+")]
    private static partial System.Text.RegularExpressions.Regex TickNumber();

    private sealed record FrameCapture(string Kinds, long TotalBytes, string Sha256)
    {
        public string[] Texts { get; init; } = [];

        public bool Equals(FrameCapture? other) => other != null && Kinds == other.Kinds && TotalBytes == other.TotalBytes && Sha256 == other.Sha256;

        public override int GetHashCode() => Sha256.GetHashCode(StringComparison.Ordinal);
    }

    private static string DescribeDifference(FrameCapture left, FrameCapture right)
    {
        for (int frame = 0; frame < Math.Min(left.Texts.Length, right.Texts.Length); frame++)
        {
            string a = left.Texts[frame];
            string b = right.Texts[frame];
            int at = 0;
            while (at < Math.Min(a.Length, b.Length) && a[at] == b[at])
            {
                at++;
            }

            if (at < Math.Max(a.Length, b.Length))
            {
                return $"frame {frame} differs at char {at}: [{a.Substring(Math.Max(0, at - 60), Math.Min(140, a.Length - Math.Max(0, at - 60)))}] vs [{b.Substring(Math.Max(0, at - 60), Math.Min(140, b.Length - Math.Max(0, at - 60)))}]";
            }
        }

        return "captures differ";
    }
}
