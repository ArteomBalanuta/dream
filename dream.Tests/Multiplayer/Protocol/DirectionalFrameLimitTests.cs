using System.Buffers.Binary;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;

namespace Dream.Tests.Multiplayer.Protocol;

// NETR-7: one 1 MB limit used to cover both directions, so a client could make the server buffer
// up to 1 MB per frame while a large world (World10k) could not reach a client at all. The limits
// are now per direction: client frames 64 KB, server frames 32 MB.
public class DirectionalFrameLimitTests
{
    private const int ClientCap = 64 * 1024;
    private const int ServerCap = 32 * 1024 * 1024;

    [Fact]
    public void Defaults_AreSmallClientFrames_AndLargeServerFrames()
    {
        FrameLimitOptions limits = FrameLimitOptions.Default;
        Assert.Equal(ClientCap, limits.ClientToServerMaxPayloadBytes);
        Assert.Equal(ServerCap, limits.ServerToClientMaxPayloadBytes);

        LengthPrefixedMessageFramer server = limits.CreateServerFramer();
        Assert.Equal(ClientCap, server.MaxReadPayloadBytes);
        Assert.Equal(ServerCap, server.MaxWritePayloadBytes);

        LengthPrefixedMessageFramer client = limits.CreateClientFramer();
        Assert.Equal(ServerCap, client.MaxReadPayloadBytes);
        Assert.Equal(ClientCap, client.MaxWritePayloadBytes);

        // The single-limit constructor keeps covering both directions.
        LengthPrefixedMessageFramer symmetric = new(1234);
        Assert.Equal(1234, symmetric.MaxReadPayloadBytes);
        Assert.Equal(1234, symmetric.MaxWritePayloadBytes);
        Assert.Equal(LengthPrefixedMessageFramer.DefaultMaxPayloadBytes, new LengthPrefixedMessageFramer().MaxReadPayloadBytes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-5, 10)]
    public void NonPositiveLimits_AreRejected(int read, int write)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LengthPrefixedMessageFramer(read, write));
    }

    [Fact]
    public void ServerRead_AcceptsAFrameAtTheClientCap_AndRejectsOneByteMore_FromTheHeaderAlone()
    {
        LengthPrefixedMessageFramer server = FrameLimitOptions.Default.CreateServerFramer();

        // At the cap: the header is valid, the frame is just not complete yet.
        Assert.False(server.TryReadFrameBounds(Header(ClientCap), out _, out _, out _));
        byte[] full = FrameLimitOptions.Default.CreateClientFramer().Frame(new byte[ClientCap]);
        Assert.True(server.TryReadFrameBounds(full, out int offset, out int length, out int consumed));
        Assert.Equal((4, ClientCap, ClientCap + 4), (offset, length, consumed));

        // One byte over: rejected with only the 4 header bytes present.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => server.TryReadFrameBounds(Header(ClientCap + 1), out _, out _, out _));
        Assert.Contains($"exceeds configured maximum {ClientCap} bytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientRead_AcceptsAFrameAboveTheOldOneMegabyteLimit_AndRejectsAboveTheServerCap()
    {
        LengthPrefixedMessageFramer client = FrameLimitOptions.Default.CreateClientFramer();
        LengthPrefixedMessageFramer server = FrameLimitOptions.Default.CreateServerFramer();
        byte[] payload = new byte[3 * 1024 * 1024];
        payload[^1] = 42;

        byte[] frame = server.Frame(payload);
        Assert.True(client.TryDeframe(frame, out byte[]? received, out int consumed));
        Assert.Equal(frame.Length, consumed);
        Assert.Equal(payload.Length, received!.Length);
        Assert.Equal(42, received[^1]);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => client.TryReadFrameBounds(Header(ServerCap + 1), out _, out _, out _));
        Assert.Contains($"exceeds configured maximum {ServerCap} bytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Writes_AreCappedPerDirection_WithoutBuildingTheFrame()
    {
        LengthPrefixedMessageFramer client = FrameLimitOptions.Default.CreateClientFramer();
        LengthPrefixedMessageFramer server = FrameLimitOptions.Default.CreateServerFramer();
        byte[] header = new byte[4];

        client.WriteHeader(header, ClientCap);
        Assert.Equal(ClientCap, BinaryPrimitives.ReadInt32LittleEndian(header));
        Assert.Throws<InvalidOperationException>(() => client.WriteHeader(header, ClientCap + 1));
        Assert.Throws<InvalidOperationException>(() => client.Frame(new byte[ClientCap + 1]));

        server.WriteHeader(header, ServerCap);
        Assert.Equal(ServerCap, BinaryPrimitives.ReadInt32LittleEndian(header));
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => server.WriteHeader(header, ServerCap + 1));
        Assert.Contains($"exceeds configured maximum {ServerCap} bytes", ex.Message, StringComparison.Ordinal);
    }

    // A hostile header must not make the receiver reserve the declared size: the receive buffer
    // holds only the bytes that arrived, and the frame is rejected before any growth.
    [Theory]
    [InlineData(ClientCap + 1)]
    [InlineData(int.MaxValue)]
    public void OversizedDeclaredFrame_FaultsWithoutAllocatingTheDeclaredSize(int declared)
    {
        LengthPrefixedMessageFramer server = FrameLimitOptions.Default.CreateServerFramer();
        FrameReceiveBuffer buffer = new();
        int capacity = buffer.Capacity;
        byte[] chunk = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(chunk, declared);

        long before = GC.GetAllocatedBytesForCurrentThread();
        buffer.Append(chunk);
        InvalidOperationException? fault = null;
        try
        {
            buffer.TryReadPayload(server, out _);
        }
        catch (InvalidOperationException ex)
        {
            fault = ex;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotNull(fault);
        Assert.Contains("exceeds configured maximum", fault!.Message, StringComparison.Ordinal);
        Assert.Equal(capacity, buffer.Capacity);
        // The exception and its message only; nowhere near the declared size.
        Assert.True(allocated < 16 * 1024, $"Allocated {allocated} B while rejecting a {declared} B frame.");
    }

    [Fact]
    public void Overrides_ParsePositiveByteCounts_AndKeepTheLimitOtherwise()
    {
        List<string> log = [];
        FrameLimitOptions overridden = FrameLimitOptions.Default.WithOverrides("4096", " 1048576 ", log.Add);
        Assert.Equal(4096, overridden.ClientToServerMaxPayloadBytes);
        Assert.Equal(1048576, overridden.ServerToClientMaxPayloadBytes);
        Assert.Empty(log);

        FrameLimitOptions unchanged = FrameLimitOptions.Default.WithOverrides(null, "  ", log.Add);
        Assert.Equal(FrameLimitOptions.Default, unchanged);
        Assert.Empty(log);

        FrameLimitOptions invalid = FrameLimitOptions.Default.WithOverrides("-1", "lots", log.Add);
        Assert.Equal(FrameLimitOptions.Default, invalid);
        Assert.Equal(2, log.Count);
        Assert.Contains(FrameLimitOptions.ClientToServerEnvironmentVariable, log[0], StringComparison.Ordinal);
        Assert.Contains(FrameLimitOptions.ServerToClientEnvironmentVariable, log[1], StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotInterval_DefaultsToTwoTicks_AndParsesOverrides()
    {
        Assert.Equal(2, ServerConnectionOptions.Default.SnapshotIntervalTicks);
        Assert.Equal(ServerConnectionOptions.DefaultSnapshotIntervalTicks, new ServerConnectionOptions().SnapshotIntervalTicks);

        List<string> log = [];
        Assert.Equal(1, ServerConnectionOptions.Default.WithSnapshotIntervalOverride("1", log.Add).SnapshotIntervalTicks);
        Assert.Equal(4, ServerConnectionOptions.Default.WithSnapshotIntervalOverride(" 4 ", log.Add).SnapshotIntervalTicks);
        Assert.Same(ServerConnectionOptions.Default, ServerConnectionOptions.Default.WithSnapshotIntervalOverride(null, log.Add));
        Assert.Empty(log);

        Assert.Same(ServerConnectionOptions.Default, ServerConnectionOptions.Default.WithSnapshotIntervalOverride("0", log.Add));
        Assert.Same(ServerConnectionOptions.Default, ServerConnectionOptions.Default.WithSnapshotIntervalOverride("fast", log.Add));
        Assert.Equal(2, log.Count);
    }

    // W9 nit: an interval long enough to trip the client's watchdog is clamped, with a log line.
    [Fact]
    public void SnapshotIntervalOverride_IsClampedToTheMaximum()
    {
        List<string> log = [];
        Assert.Equal(16, ServerConnectionOptions.MaxSnapshotIntervalOverrideTicks);
        Assert.Equal(16, ServerConnectionOptions.Default.WithSnapshotIntervalOverride("16", log.Add).SnapshotIntervalTicks);
        Assert.Empty(log);
        Assert.Equal(16, ServerConnectionOptions.Default.WithSnapshotIntervalOverride("17", log.Add).SnapshotIntervalTicks);
        Assert.Equal(16, ServerConnectionOptions.Default.WithSnapshotIntervalOverride("600", log.Add).SnapshotIntervalTicks);
        Assert.Equal(2, log.Count);
        Assert.Contains("600", log[1], StringComparison.Ordinal);
    }

    private static byte[] Header(int declaredLength)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, declaredLength);
        return header;
    }
}
