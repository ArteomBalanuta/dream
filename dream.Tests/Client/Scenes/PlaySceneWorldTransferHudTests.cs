using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Networking;
using Dream.Client.Scenes;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Client.Scenes;

// The "receiving world" HUD state: while a world transfer (join or resync) is in progress the
// client holds every message, so remote actors freeze; the HUD says so once the transfer has run
// past a short threshold, with the bytes received and the time so far, without allocating.
public sealed class PlaySceneWorldTransferHudTests(ITestOutputHelper output)
{
    private static readonly TransportJsonCodec Codec = new();

    // ---- The state ----------------------------------------------------------------------------

    [Fact]
    public void None_IsHidden()
    {
        Assert.False(WorldTransferHudState.None.IsReceiving);
        Assert.False(WorldTransferHudState.None.IsVisible);
        Assert.Equal(default, WorldTransferHudState.None);
    }

    [Fact]
    public void Indicator_IsHiddenUnderTheThreshold_AndShownFromIt()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(300), WorldTransferHudState.ShowThreshold);
        Assert.False(new WorldTransferHudState(true, 1 << 20, 3, TimeSpan.Zero).IsVisible);
        Assert.False(new WorldTransferHudState(true, 1 << 20, 3, TimeSpan.FromMilliseconds(299)).IsVisible);
        Assert.True(new WorldTransferHudState(true, 1 << 20, 3, TimeSpan.FromMilliseconds(300)).IsVisible);
        Assert.True(new WorldTransferHudState(true, 0, 0, TimeSpan.FromSeconds(17)).IsVisible);
        // Not receiving: never shown, however old the values.
        Assert.False(new WorldTransferHudState(false, 1 << 20, 3, TimeSpan.FromSeconds(17)).IsVisible);
    }

    [Theory]
    [InlineData("")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void TryFormat_ShowsKilobytesAndWholeSeconds(string cultureName)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        try
        {
            WorldTransferHudState state = new(true, (412 * 1024) + 100, 7, TimeSpan.FromSeconds(6.7));
            Span<byte> buffer = stackalloc byte[HudText.StackBufferBytes];

            Assert.True(state.TryFormat(buffer, out int written));
            Assert.Equal("Receiving world... 412 KB, 6 s", Encoding.UTF8.GetString(buffer[..written]));
            Assert.Equal("Receiving world... 412 KB, 6 s", state.Format());
            Assert.False(state.TryFormat(stackalloc byte[8], out _));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TryFormat_AllocatesNothing()
    {
        WorldTransferHudState state = new(true, 9_876_543, 12, TimeSpan.FromSeconds(17.2));
        long bytes = AllocationAssert.AtMost(0, () =>
        {
            Span<byte> buffer = stackalloc byte[HudText.StackBufferBytes];
            if (!state.IsVisible || !state.TryFormat(buffer, out _))
            {
                throw new InvalidOperationException("The indicator did not format.");
            }
        });
        output.WriteLine($"World transfer HUD line: {bytes} B/frame");
    }

    // ---- The session's progress, as the HUD sees it --------------------------------------------

    [Fact]
    public async Task HudState_FollowsTheSessionTransfer_FromCarrierToFinalChunk()
    {
        ManualTimeProvider clock = new();
        GatedTransport transport = new("world-transfer-hud");
        await using RemoteClientSession session = CreateSession(transport, clock);
        session.StartBackgroundReceive();
        Assert.Equal(WorldTransferHudState.None, PlayScene.CaptureWorldTransferHudState(session));

        // A plain snapshot before any transfer: nothing to show.
        ulong sequence = 1;
        transport.Feed(Frame(Payload(Snapshot(1), sequence++)));
        await WaitUntilAsync(() => session.InboundFrameCount == 1);
        Assert.False(session.TryGetWorldTransferProgress(out _, out _, out _));
        Assert.Equal(WorldTransferHudState.None, PlayScene.CaptureWorldTransferHudState(session));

        byte[] carrier = Payload(Carrier(5), sequence++);
        transport.Feed(Frame(carrier));
        await WaitUntilAsync(() => session.InboundFrameCount == 2);
        WorldTransferHudState state = PlayScene.CaptureWorldTransferHudState(session);
        Assert.True(session.IsReceivingWorld);
        Assert.True(state.IsReceiving);
        Assert.Equal(carrier.Length, state.BytesReceived);
        Assert.Equal(0, state.ChunksReceived);
        Assert.Equal(TimeSpan.Zero, state.Elapsed);
        Assert.False(state.IsVisible); // A fast join never flashes the indicator.

        clock.Advance(TimeSpan.FromMilliseconds(299));
        byte[] chunk0 = Payload(Chunk(5, 0, final: false, Cubes(8, 8, 8, 4)), sequence++);
        transport.Feed(Frame(chunk0));
        await WaitUntilAsync(() => session.InboundFrameCount == 3);
        state = PlayScene.CaptureWorldTransferHudState(session);
        Assert.Equal(carrier.Length + chunk0.Length, state.BytesReceived);
        Assert.Equal(1, state.ChunksReceived);
        Assert.Equal(TimeSpan.FromMilliseconds(299), state.Elapsed);
        Assert.False(state.IsVisible);

        // Snapshots held during the transfer are not world bytes.
        clock.Advance(TimeSpan.FromSeconds(6));
        transport.Feed(Frame(Payload(Snapshot(2), sequence++)));
        byte[] chunk1 = Payload(Chunk(5, 1, final: false, Cubes(8, 8, 8, 4)), sequence++);
        transport.Feed(Frame(chunk1));
        await WaitUntilAsync(() => session.InboundFrameCount == 5);
        state = PlayScene.CaptureWorldTransferHudState(session);
        Assert.True(state.IsVisible);
        Assert.Equal(carrier.Length + chunk0.Length + chunk1.Length, state.BytesReceived);
        Assert.Equal(2, state.ChunksReceived);
        Assert.Equal(6, state.WholeSecondsElapsed);
        Assert.True(session.TryGetWorldTransferProgress(out long bytes, out int chunks, out TimeSpan elapsed));
        Assert.Equal((state.BytesReceived, state.ChunksReceived, state.Elapsed), (bytes, chunks, elapsed));

        transport.Feed(Frame(Payload(Chunk(5, 2, final: true, Cubes(8, 8, 8, 4)), sequence++)));
        await WaitUntilAsync(() => session.WorldTransfersCompleted == 1 || !session.IsConnected);
        Assert.True(session.IsConnected, session.DisconnectReason);
        Assert.False(session.IsReceivingWorld);
        Assert.Equal(WorldTransferHudState.None, PlayScene.CaptureWorldTransferHudState(session));

        // The next transfer (a resync) starts its count afresh.
        byte[] resync = Payload(Carrier(6), sequence);
        transport.Feed(Frame(resync));
        await WaitUntilAsync(() => session.IsReceivingWorld);
        await WaitUntilAsync(() => PlayScene.CaptureWorldTransferHudState(session).BytesReceived == resync.Length);
        Assert.Equal(0, PlayScene.CaptureWorldTransferHudState(session).ChunksReceived);
    }

    // The per-frame path while receiving: capture the state from the session, test it, format it.
    [Fact]
    public async Task HudFramePath_WhileReceivingAWorld_AllocatesNothing()
    {
        ManualTimeProvider clock = new();
        GatedTransport transport = new("world-transfer-hud-alloc");
        await using RemoteClientSession session = CreateSession(transport, clock);
        session.StartBackgroundReceive();
        transport.Feed(Frame(Payload(Carrier(9), 1)));
        transport.Feed(Frame(Payload(Chunk(9, 0, final: false, Cubes(8, 8, 8, 4)), 2)));
        await WaitUntilAsync(() => session.InboundFrameCount == 2);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.True(PlayScene.CaptureWorldTransferHudState(session).IsVisible);

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            WorldTransferHudState state = PlayScene.CaptureWorldTransferHudState(session);
            Span<byte> buffer = stackalloc byte[HudText.StackBufferBytes];
            if (!state.IsVisible || !state.TryFormat(buffer, out _))
            {
                throw new InvalidOperationException("The indicator did not format.");
            }
        });
        output.WriteLine($"Receiving-world HUD frame path: {bytes} B/frame");
    }

    // ---- Structure ------------------------------------------------------------------------------

    // The indicator is drawn in the HUD pass (DrawHUD -> DrawHudInterface), from Render's
    // WorldTransferHud, which PlayScene.Render sets from the session every frame before the HUD.
    [Fact]
    public void Indicator_IsDrawnInTheHudPass_FromTheSessionState()
    {
        string render = PartialClassSource.Read(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Render.cs"));
        string drawHud = MethodBody(render, "public HudFrameResult DrawHUD(");
        Assert.Matches(new Regex(@"DrawHudInterface\([^;]*performance,\s*WorldTransferHud,\s*inventory\);"), drawHud);
        Assert.True(
            drawHud.IndexOf("DrawHudInterface(", StringComparison.Ordinal) < drawHud.IndexOf("ImGui.Render()", StringComparison.Ordinal),
            "the HUD interface is drawn before the ImGui frame is rendered");

        string hudInterface = MethodBody(render, "private HudInteractionRequest DrawHudInterface(");
        Assert.Matches(new Regex(@"DrawWorldTransferIndicator\(worldTransfer, hudText\);"), hudInterface);
        Assert.True(
            hudInterface.IndexOf("ImGui.Begin(\"HUD\"", StringComparison.Ordinal) < hudInterface.IndexOf("DrawWorldTransferIndicator(", StringComparison.Ordinal) &&
            hudInterface.IndexOf("DrawWorldTransferIndicator(", StringComparison.Ordinal) < hudInterface.IndexOf("ImGui.End();", StringComparison.Ordinal),
            "the indicator is inside the HUD window");

        string indicator = MethodBody(render, "private static void DrawWorldTransferIndicator(");
        Assert.Contains("if (!worldTransfer.IsVisible)", indicator);
        Assert.Contains("worldTransfer.TryFormat(hudText, out int lineBytes)", indicator);
        Assert.Contains("HudText.TextUnformatted(hudText[..lineBytes]);", indicator);

        string playScene = PartialClassSource.Read(Path.Combine(RepoRoot(), "dream", "Client", "Scenes", "PlayScene.cs"));
        string sceneRender = MethodBody(playScene, "public void Render(FrameEventArgs args");
        int set = sceneRender.IndexOf("_render.WorldTransferHud = CaptureWorldTransferHudState(_remoteClient);", StringComparison.Ordinal);
        Assert.True(set >= 0, "PlayScene.Render sets the HUD's world transfer state");
        Assert.True(set < sceneRender.IndexOf("_render.Hud.DrawFrame(", StringComparison.Ordinal), "it is set before the HUD pass");
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private static RemoteClientSession CreateSession(GatedTransport transport, ManualTimeProvider clock)
    {
        return new RemoteClientSession(
            transport,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(1 << 20),
            new RemoteClientOptions("127.0.0.1", 1, "session-a", "A", "world-transfer-hud"),
            clock,
            WorldTransferLimits.Default);
    }

    private static ServerSnapshotMessage Carrier(int transferId)
    {
        return new ServerSnapshotMessage(CreateSnapshot(1, new Dictionary<string, ulong>(), []), transferId);
    }

    private static ServerSnapshotMessage Snapshot(ulong tick)
    {
        return new ServerSnapshotMessage(CreateSnapshot(tick, new Dictionary<string, ulong>(), []));
    }

    private static WorldChunkMessage Chunk(int transferId, int index, bool final, WorldSnapshot world)
    {
        return new WorldChunkMessage(transferId, index, final, world);
    }

    private static WorldSnapshot Cubes(int width, int height, int depth, int count)
    {
        PlacedCubeSnapshot[] cubes = [.. Enumerable.Range(0, count).Select(i =>
        {
            Vector3 cell = new(i % 7, 1, i / 7);
            return new PlacedCubeSnapshot(cell, cell + new Vector3(0.5f), GeometryMaterialId.Rock, 1000 + i);
        })];
        return new WorldSnapshot(
            width,
            height,
            depth,
            [.. cubes.Select(cube => cube.CellPosition)],
            [],
            [],
            cubes,
            [],
            MutationSequence: 5);
    }

    private static byte[] Payload<TMessage>(TMessage message, ulong sequence)
        where TMessage : IServerToClientMessage
    {
        return Codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence), message);
    }

    private static byte[] Frame(byte[] payload)
    {
        return new LengthPrefixedMessageFramer(1 << 20).Frame(payload);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(5);
        }
    }

    // The text from a method's signature to its closing brace (brace-matched).
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not found");
        int open = source.IndexOf('{', source.IndexOf(')', start));
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"'{signature}' has no closing brace");
    }

    private static string RepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
