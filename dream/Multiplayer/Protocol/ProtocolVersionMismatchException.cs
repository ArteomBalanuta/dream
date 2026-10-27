using System.Text.Json;

namespace Dream.Multiplayer.Protocol;

// The peer speaks a different protocol version than this build. Its message names both versions
// from the point of view of the side that detected it, so it can be shown to the player or logged
// by the server as is. Derives from InvalidOperationException, which the codec throws for the same
// condition, so existing handlers still catch it.
public sealed class ProtocolVersionMismatchException : InvalidOperationException
{
    private ProtocolVersionMismatchException(int peerVersion, int localVersion, string message, Exception? innerException)
        : base(message, innerException)
    {
        PeerVersion = peerVersion;
        LocalVersion = localVersion;
    }

    public int PeerVersion { get; }

    public int LocalVersion { get; }

    // Detected by a client: the server speaks another version.
    public static ProtocolVersionMismatchException FromServer(int serverVersion, Exception? innerException = null)
    {
        return new ProtocolVersionMismatchException(
            serverVersion,
            TransportEnvelope.CurrentProtocolVersion,
            $"Server uses protocol v{serverVersion}; this client uses v{TransportEnvelope.CurrentProtocolVersion}.",
            innerException);
    }

    // Detected by the server: the client speaks another version.
    public static ProtocolVersionMismatchException FromClient(int clientVersion, Exception? innerException = null)
    {
        return new ProtocolVersionMismatchException(
            clientVersion,
            TransportEnvelope.CurrentProtocolVersion,
            $"Client uses protocol v{clientVersion}; this server uses v{TransportEnvelope.CurrentProtocolVersion}.",
            innerException);
    }

    // Reads only the top-level protocolVersion of an envelope payload. Every protocol version so
    // far puts it there, so this works on a frame the codec refuses to decode. Returns false when
    // the payload is not a JSON object or has no integer protocolVersion.
    public static bool TryReadProtocolVersion(ReadOnlySpan<byte> payload, out int protocolVersion)
    {
        protocolVersion = 0;
        try
        {
            Utf8JsonReader reader = new(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("protocolVersion"u8))
                {
                    return reader.Read() &&
                        reader.TokenType == JsonTokenType.Number &&
                        reader.TryGetInt32(out protocolVersion);
                }

                reader.Read();
                reader.Skip();
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    // The payload's protocolVersion when it is readable and differs from ours, otherwise null. A
    // decoder that failed calls this to tell a version mismatch from a malformed frame.
    public static int? ReadForeignProtocolVersion(ReadOnlySpan<byte> payload)
    {
        return TryReadProtocolVersion(payload, out int version) &&
            version != TransportEnvelope.CurrentProtocolVersion
            ? version
            : null;
    }
}
