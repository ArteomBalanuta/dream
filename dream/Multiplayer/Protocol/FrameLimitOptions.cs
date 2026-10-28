using System.Globalization;

namespace Dream.Multiplayer.Protocol;

// Largest frame payload per direction. A client only ever sends small frames (hello, join,
// one command per tick, actions, keepalives, resync requests), so the server reads with a small
// cap that bounds what a hostile peer can make it buffer. The server sends snapshots and world
// chunks, so the client reads with a larger cap. Each side writes with the cap its peer reads
// with, so an oversized frame fails at its writer with a clear reason instead of reaching the peer.
//
// Since chunked world transfer (protocol v3, SCALE-1) no frame carries a whole world: a join or
// resync world goes out as a carrier plus WorldChunkMessage frames of about 64 KB. The largest
// legitimate server frame is now a lagging client's coalesced snapshot at the merge caps
// (ServerConnectionOptions: 4096 world mutations and 4096 damage events; past them the deltas are
// dropped for a resync). Measured worst case (WorldTransferLimitTests: every mutation a fracture
// with six moved pieces, every number at full length, 320 actors): 2.74 MB. A World85k chunk is
// about 65 KB and its carrier under 200 B. The 8 MB cap keeps about 3x headroom over that worst
// case (4 MB would leave 1.5x, and one tick's snapshot is not capped by the merge limits) and
// bounds what a client buffers for one frame to a quarter of the 32 MB stopgap that whole-world
// frames needed.
public sealed record FrameLimitOptions
{
    public const int DefaultClientToServerMaxPayloadBytes = 64 * 1024;
    public const int DefaultServerToClientMaxPayloadBytes = 8 * 1024 * 1024;

    // Overrides ClientToServerMaxPayloadBytes (bytes, a positive integer) for one run.
    public const string ClientToServerEnvironmentVariable = "DREAM_CLIENT_FRAME_LIMIT_BYTES";

    // Overrides ServerToClientMaxPayloadBytes (bytes, a positive integer) for one run.
    public const string ServerToClientEnvironmentVariable = "DREAM_SERVER_FRAME_LIMIT_BYTES";

    public static FrameLimitOptions Default { get; } = new();

    // Frames a client writes and the server reads.
    public int ClientToServerMaxPayloadBytes { get; init; } = DefaultClientToServerMaxPayloadBytes;

    // Frames the server writes and a client reads.
    public int ServerToClientMaxPayloadBytes { get; init; } = DefaultServerToClientMaxPayloadBytes;

    // Reads with the client-to-server cap and writes with the server-to-client cap.
    public LengthPrefixedMessageFramer CreateServerFramer()
    {
        return new LengthPrefixedMessageFramer(
            maxReadPayloadBytes: ClientToServerMaxPayloadBytes,
            maxWritePayloadBytes: ServerToClientMaxPayloadBytes);
    }

    // Reads with the server-to-client cap and writes with the client-to-server cap.
    public LengthPrefixedMessageFramer CreateClientFramer()
    {
        return new LengthPrefixedMessageFramer(
            maxReadPayloadBytes: ServerToClientMaxPayloadBytes,
            maxWritePayloadBytes: ClientToServerMaxPayloadBytes);
    }

    public static FrameLimitOptions FromEnvironment(Action<string>? log = null)
    {
        return Default.WithOverrides(
            Environment.GetEnvironmentVariable(ClientToServerEnvironmentVariable),
            Environment.GetEnvironmentVariable(ServerToClientEnvironmentVariable),
            log);
    }

    // An unset value keeps the current limit; an invalid one (not a positive integer) keeps it
    // with a log line.
    public FrameLimitOptions WithOverrides(string? clientToServer, string? serverToClient, Action<string>? log = null)
    {
        return this with
        {
            ClientToServerMaxPayloadBytes = Resolve(ClientToServerEnvironmentVariable, clientToServer, ClientToServerMaxPayloadBytes, log),
            ServerToClientMaxPayloadBytes = Resolve(ServerToClientEnvironmentVariable, serverToClient, ServerToClientMaxPayloadBytes, log)
        };
    }

    private static int Resolve(string name, string? value, int current, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return current;
        }

        if (int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
        {
            return parsed;
        }

        log?.Invoke($"{name} is not a positive integer byte count; keeping {current} bytes.");
        return current;
    }
}
