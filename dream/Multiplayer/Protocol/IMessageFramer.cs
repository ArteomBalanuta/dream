namespace Dream.Multiplayer.Protocol;

public interface IMessageFramer
{
    // Bytes reserved in front of every payload; WriteHeader fills exactly this many.
    int HeaderLength { get; }

    byte[] Frame(ReadOnlySpan<byte> payload);

    // Writes the frame header for a payload that already sits right after it, so a frame can be
    // built in place. Enforces the same maximum payload size, with the same exception, as Frame.
    void WriteHeader(Span<byte> header, int payloadLength);

    bool TryDeframe(ReadOnlySpan<byte> buffer, out byte[]? payload, out int bytesConsumed);

    // Locates the next complete frame without copying; offsets are relative to the start of buffer.
    bool TryReadFrameBounds(ReadOnlySpan<byte> buffer, out int payloadOffset, out int payloadLength, out int bytesConsumed);
}
