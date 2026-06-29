public interface IMessageFramer
{
    byte[] Frame(ReadOnlySpan<byte> payload);

    bool TryDeframe(ReadOnlySpan<byte> buffer, out byte[]? payload, out int bytesConsumed);
}
