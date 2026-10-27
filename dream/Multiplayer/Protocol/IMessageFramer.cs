namespace Dream.Multiplayer.Protocol;

public interface IMessageFramer
{
    byte[] Frame(ReadOnlySpan<byte> payload);

    bool TryDeframe(ReadOnlySpan<byte> buffer, out byte[]? payload, out int bytesConsumed);

    // Locates the next complete frame without copying; offsets are relative to the start of buffer.
    bool TryReadFrameBounds(ReadOnlySpan<byte> buffer, out int payloadOffset, out int payloadLength, out int bytesConsumed);
}
