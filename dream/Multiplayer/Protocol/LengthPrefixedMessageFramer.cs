using System.Buffers.Binary;

namespace Dream.Multiplayer.Protocol;


public sealed class LengthPrefixedMessageFramer : IMessageFramer
{
    private const int HeaderLength = sizeof(int);
    private readonly int _maxPayloadBytes;

    public LengthPrefixedMessageFramer(int maxPayloadBytes = 1024 * 1024)
    {
        if (maxPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes), "Maximum payload must be positive.");
        }

        _maxPayloadBytes = maxPayloadBytes;
    }

    public byte[] Frame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > _maxPayloadBytes)
        {
            throw new InvalidOperationException(
                $"Payload length {payload.Length} exceeds configured maximum {_maxPayloadBytes} bytes.");
        }

        byte[] framed = new byte[HeaderLength + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed, payload.Length);
        payload.CopyTo(framed.AsSpan(HeaderLength));
        return framed;
    }

    public bool TryDeframe(ReadOnlySpan<byte> buffer, out byte[]? payload, out int bytesConsumed)
    {
        payload = null;
        if (!TryReadFrameBounds(buffer, out int payloadOffset, out int payloadLength, out bytesConsumed))
        {
            return false;
        }

        payload = buffer.Slice(payloadOffset, payloadLength).ToArray();
        return true;
    }

    public bool TryReadFrameBounds(ReadOnlySpan<byte> buffer, out int payloadOffset, out int payloadLength, out int bytesConsumed)
    {
        payloadOffset = 0;
        payloadLength = 0;
        bytesConsumed = 0;

        if (buffer.Length < HeaderLength)
        {
            return false;
        }

        int declaredLength = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        if (declaredLength < 0)
        {
            throw new InvalidOperationException("Negative frame length is invalid.");
        }

        if (declaredLength > _maxPayloadBytes)
        {
            throw new InvalidOperationException(
                $"Frame payload length {declaredLength} exceeds configured maximum {_maxPayloadBytes} bytes.");
        }

        if (declaredLength > int.MaxValue - HeaderLength)
        {
            throw new InvalidOperationException("Frame length overflow is invalid.");
        }

        int totalLength = HeaderLength + declaredLength;
        if (buffer.Length < totalLength)
        {
            return false;
        }

        payloadOffset = HeaderLength;
        payloadLength = declaredLength;
        bytesConsumed = totalLength;
        return true;
    }
}
