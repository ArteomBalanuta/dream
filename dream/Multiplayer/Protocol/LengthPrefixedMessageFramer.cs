using System.Buffers.Binary;

namespace Dream.Multiplayer.Protocol;


public sealed class LengthPrefixedMessageFramer : IMessageFramer
{
    private const int PrefixLength = sizeof(int);
    private readonly int _maxPayloadBytes;

    public LengthPrefixedMessageFramer(int maxPayloadBytes = 1024 * 1024)
    {
        if (maxPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes), "Maximum payload must be positive.");
        }

        _maxPayloadBytes = maxPayloadBytes;
    }

    public int HeaderLength => PrefixLength;

    public byte[] Frame(ReadOnlySpan<byte> payload)
    {
        EnsurePayloadFits(payload.Length);
        byte[] framed = new byte[PrefixLength + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed, payload.Length);
        payload.CopyTo(framed.AsSpan(PrefixLength));
        return framed;
    }

    public void WriteHeader(Span<byte> header, int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        if (header.Length < PrefixLength)
        {
            throw new ArgumentException($"Header span must be at least {PrefixLength} bytes.", nameof(header));
        }

        EnsurePayloadFits(payloadLength);
        BinaryPrimitives.WriteInt32LittleEndian(header, payloadLength);
    }

    private void EnsurePayloadFits(int payloadLength)
    {
        if (payloadLength > _maxPayloadBytes)
        {
            throw new InvalidOperationException(
                $"Payload length {payloadLength} exceeds configured maximum {_maxPayloadBytes} bytes.");
        }
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

        if (buffer.Length < PrefixLength)
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

        if (declaredLength > int.MaxValue - PrefixLength)
        {
            throw new InvalidOperationException("Frame length overflow is invalid.");
        }

        int totalLength = PrefixLength + declaredLength;
        if (buffer.Length < totalLength)
        {
            return false;
        }

        payloadOffset = PrefixLength;
        payloadLength = declaredLength;
        bytesConsumed = totalLength;
        return true;
    }
}
