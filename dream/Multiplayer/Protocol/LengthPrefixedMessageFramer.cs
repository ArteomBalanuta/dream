using System.Buffers.Binary;

namespace Dream.Multiplayer.Protocol;


// A 4-byte little-endian payload length, then the payload. The read and write limits are separate
// because the two directions carry very different frames: a client sends small commands and
// actions, while a server sends snapshots and whole worlds (see FrameLimitOptions). A frame whose
// declared length passes the read limit is rejected from its 4-byte header alone, before any of
// its payload is buffered, so a hostile length cannot make the receiver allocate it.
public sealed class LengthPrefixedMessageFramer : IMessageFramer
{
    // The limit of the single-argument constructor, applied to both directions.
    public const int DefaultMaxPayloadBytes = 1024 * 1024;

    private const int PrefixLength = sizeof(int);
    private readonly int _maxReadPayloadBytes;
    private readonly int _maxWritePayloadBytes;

    public LengthPrefixedMessageFramer(int maxPayloadBytes = DefaultMaxPayloadBytes)
        : this(maxPayloadBytes, maxPayloadBytes)
    {
    }

    public LengthPrefixedMessageFramer(int maxReadPayloadBytes, int maxWritePayloadBytes)
    {
        if (maxReadPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxReadPayloadBytes), "Maximum payload must be positive.");
        }

        if (maxWritePayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWritePayloadBytes), "Maximum payload must be positive.");
        }

        _maxReadPayloadBytes = maxReadPayloadBytes;
        _maxWritePayloadBytes = maxWritePayloadBytes;
    }

    // Largest payload TryDeframe and TryReadFrameBounds accept.
    public int MaxReadPayloadBytes => _maxReadPayloadBytes;

    // Largest payload Frame and WriteHeader accept.
    public int MaxWritePayloadBytes => _maxWritePayloadBytes;

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
        if (payloadLength > _maxWritePayloadBytes)
        {
            throw new InvalidOperationException(
                $"Payload length {payloadLength} exceeds configured maximum {_maxWritePayloadBytes} bytes.");
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

        if (declaredLength > _maxReadPayloadBytes)
        {
            // Checked from the 4-byte header alone, before the payload is buffered.
            throw new InvalidOperationException(
                $"Frame payload length {declaredLength} exceeds configured maximum {_maxReadPayloadBytes} bytes.");
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
