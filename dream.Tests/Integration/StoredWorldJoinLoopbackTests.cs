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
    // Measured at wave E C0 (3e30c12, protocol v8) before A, B or C merged (the protocol digit is
    // normalised to 8 below, so wave G's v9 changes nothing else in these frames): a client joins the
    // generated 56x8x56 world of StoredWorldDb(stored: false) (seed 20261029) and the server ticks
    // three more times with a snapshot per tick; the pinned frames are the join's (the snapshot frames
    // are checked to arrive but not byte-compared, see CaptureGeneratedJoinAsync). Generated worlds send no terrain manifest and no
    // terrain frames, so every frame must stay byte-identical (plan rule 4, decision E7).
    // Re-pinned in the last fix loop of wave I, and the only cause is the day wolf's spawn in the join's actor
    // list: (5.5, 1, 2.5) became (52.5, 1, 52.5), 2 bytes longer in all (8,492 -> 8,494). Sha-256 was
    // 40391109031e35d0..., now e98ef020127c415a....
    // Re-pinned again by classic HUD iteration 2 (protocol v13), whose compact inventory snapshot replaces
    // the join's whole inventory object: it is cut out below (StripInventory) and pinned apart, by
    // StarterKitInventoryBytesTests and PlayerInventorySnapshotConverterTests, so the pin covers every
    // other byte of the three frames. Sha-256 was e98ef020127c415a..., now df39f10be444bb95....  The join is 6,031 B in all.
    // Re-pinned by the lowered experience curve (migration 0011: 100 + level * 50): the join's player progression
    // reads experienceToNextLevel 150 instead of 1023 at level 1, one digit shorter. Sha-256 was df39f10be444bb95...,
    // now 73d2a7482a21a23b.... The join is 6,030 B in all.
    // Re-pinned by the stats sheet merge (protocol 19): the player's combatStats object carries
    // ,"agility":10,"energy":10 (26 B) and the player actor its "visualKey":"held.tool.builder|" (32 B,
    // the worn main hand and empty off hand). Sha-256 was 73d2a7482a21a23b..., now c1ba86757bab6b22....
    // The join is 6,088 B in all.
    // Re-pinned by the starter kit's off-hand torch (items.json "equip": "OffHand"): the player actor's
    // visualKey reads "held.tool.builder|held.tool.torch" and it now carries a held light, 29 B in all.
    // Sha-256 was c1ba86757bab6b22..., now 90a7bf84de99b7b7.... The join is 6,117 B in all.
    // The new-character start was 1000 for a while (a39702e6; pinned 6,127 B, sha-256 e9dfe27f584dcbcb...):
    // four attributes two digits longer ("1000" for "10", 4 x 2 = 8 B) and the two health values one digit
    // longer (5050 for 100, 2 x 1 = 2 B), +10 B. Rolling the start back to 10 (NewCharacterAttribute) removes
    // exactly those 10 B and returns the earlier pin: 6,127 -> 6,117 B, and the sha-256 is again
    // 90a7bf84de99b7b7... byte for byte. This test does not seed the baseline on purpose: it pins what a
    // new character's join looks like.
    //
    // Re-measured on the tree that carries BOTH the attribute-origin rollback and the inventory-drop
    // protocol bump (19 -> 20), because each wave moved this pin on its own branch and the combined value
    // is neither branch's. Measured: 6,117 B, sha-256 90a7bf84de99b7b7.... Accounted for in full:
    //   6,117 B  the pre-a39702e6 pin, restored by the rollback above (6,127 - 10 B)
    //     + 0 B  the 19 -> 20 protocol bump
    //   -------
    //   6,117 B  measured
    // The protocol bump contributes nothing at all here, and not merely because the digit is the same
    // width: CaptureGeneratedJoinAsync below normalises "protocolVersion":21 to "protocolVersion":8 on
    // every frame before it hashes or counts, exactly as it previously normalised ":19" to ":8". Both
    // versions collapse to identical normalised text, so the bump moves neither the byte count nor the
    // sha-256. That is why the hash here is byte-for-byte the attribute wave's 90a7bf84... rather than a
    // new value -- a protocol bump alone can never re-pin this test, only a payload change can.
    // The join is 6,117 B in all.
    private const int C0FrameCount = 3;
    private const long C0TotalBytes = 6_117;
    private const string C0FrameKinds =
        "server.hello,server.join-accepted,server.world-chunk";
    private const string C0Sha256 = "90a7bf84de99b7b72c5cca0a84403fbd3406d725b036b7872286cdf241324e36";
    private const int JoinAcceptedGateBytes = 4_099;

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

        // The real frame, before any normalisation. Wave H's gate is 3,565 B + 15%.
        output.WriteLine($"server.join-accepted, starter kit: {first.JoinAcceptedBytes:N0} B");
        Assert.True(first.JoinAcceptedBytes <= JoinAcceptedGateBytes, $"{first.JoinAcceptedBytes} B");
    }

    // The normalisation may cut out only the inventory object of a join-accepted frame, whole, and must
    // leave every other byte (including text that merely looks like it) alone.
    [Theory]
    [InlineData(
        "{\"a\":1,\"inventory\":{\"playerName\":\"p\",\"bag\":[[0,1,\"k\",0,[]]],\"worn\":[]},\"b\":2}",
        "{\"a\":1,\"inventory\":{},\"b\":2}")]
    [InlineData(
        "{\"inventory\":{\"playerName\":\"} \\\" {\",\"bag\":[],\"worn\":[]},\"roster\":[]}",
        "{\"inventory\":{},\"roster\":[]}")]
    [InlineData(
        "{\"roster\":[{\"displayName\":\"inventory\"}],\"x\":{}}",
        "{\"roster\":[{\"displayName\":\"inventory\"}],\"x\":{}}")]
    [InlineData(
        "{\"a\":1,\"inventory\":null,\"b\":2}",
        "{\"a\":1,\"inventory\":null,\"b\":2}")]
    public void TheInventoryNormalisation_CutsOutOnlyTheInventoryObject(string frame, string expected)
    {
        Assert.Equal(expected, StripInventory(frame));
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

            SqlitePools.Release(db.DatabasePath);
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
            // Protocols v9 (wave G), v10 (wave H), v11 (wave I), v12 (classic HUD) and v13 change only the
            // version digit of these frames, apart from the inventory object of join-accepted (v10 added item
            // fields to it and v13 rewrote it as the compact snapshot); compare as v8 with the inventory cut
            // out, so every other byte still matches the capture. The inventory's size and shape are pinned by
            // StarterKitInventoryBytesTests and PlayerInventorySnapshotConverterTests.
            // KEEP THIS LITERAL IN STEP WITH TransportEnvelope.CurrentProtocolVersion. It is not a guarantee that
            // a protocol bump cannot move the pin - it is the maintenance that makes that true. Miss it and the
            // Replace is a silent no-op: the digit stays in the hashed bytes, the sha256 and byte count move, and
            // it presents as a PAYLOAD regression rather than as a stale search pattern, which is a long hunt for
            // nothing. The same stale-pattern bug on the downgrade helpers in JoinAcceptedWorldDescriptorCodecTests
            // and WaveBTesterWorldsLoopbackTests has the opposite symptom - those go green while verifying nothing -
            // so after ANY bump, grep Replace("\"protocolVersion across the test tree even if the suite is green.
            string text = StripInventory(
                TickNumber().Replace(System.Text.Encoding.UTF8.GetString(frame.Payload), "\"$1\":0").Replace("\"protocolVersion\":21", "\"protocolVersion\":8"));
            texts.Add(text);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
            total += bytes.Length;
        }

        string kinds = string.Join(",", frames.Select(frame => frame.Decoded.Envelope.MessageKind));
        return new FrameCapture(kinds, total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant())
        {
            Texts = [.. texts],
            JoinAcceptedBytes = frames.Single(frame => frame.Decoded.Envelope.MessageKind == "server.join-accepted").Payload.Length
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"""(currentServerTick|serverTick)"":\d+")]
    private static partial System.Text.RegularExpressions.Regex TickNumber();

    // Replaces the value of the "inventory" property, when it is an object, with {}. The object is found by
    // counting braces outside strings, so a brace or an escaped quote in a name cannot end it early.
    private static string StripInventory(string json)
    {
        const string Property = "\"inventory\":{";
        int start = json.IndexOf(Property, StringComparison.Ordinal);
        if (start < 0)
        {
            return json;
        }

        int depth = 0;
        bool inString = false;
        for (int i = start + Property.Length - 1; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return string.Concat(json.AsSpan(0, start), "\"inventory\":{}", json.AsSpan(i + 1));
            }
        }

        return json;
    }

    private sealed record FrameCapture(string Kinds, long TotalBytes, string Sha256)
    {
        public string[] Texts { get; init; } = [];

        // The join-accepted frame's real payload length, before normalisation.
        public int JoinAcceptedBytes { get; init; }

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
