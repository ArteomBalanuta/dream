namespace Dream.Multiplayer.Protocol;

// Accumulates received bytes and deframes from a span with a read offset. The previous
// List<byte> + ToArray-per-frame approach copied the whole buffered backlog for every frame,
// which is quadratic in buffered bytes (large join snapshots).
public sealed class FrameReceiveBuffer
{
    private byte[] _buffer;
    private int _start;
    private int _end;

    public FrameReceiveBuffer(int initialCapacity = 16 * 1024)
    {
        _buffer = new byte[Math.Max(16, initialCapacity)];
    }

    public int BufferedBytes => _end - _start;

    public int Capacity => _buffer.Length;

    public void Append(ReadOnlySpan<byte> data)
    {
        EnsureWritable(data.Length);
        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }

    // Returns true when a complete frame was consumed; payload may be null for an empty frame.
    public bool TryReadFrame(IMessageFramer framer, out byte[]? payload)
    {
        ArgumentNullException.ThrowIfNull(framer);
        if (!framer.TryDeframe(_buffer.AsSpan(_start, _end - _start), out payload, out int consumed))
        {
            return false;
        }

        _start += consumed;
        if (_start == _end)
        {
            _start = 0;
            _end = 0;
        }

        return true;
    }

    // Zero-copy variant: payload aliases the internal buffer and is valid only until the next Append.
    // An empty frame is reported as consumed with an empty payload.
    public bool TryReadPayload(IMessageFramer framer, out ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(framer);
        ReadOnlySpan<byte> buffered = _buffer.AsSpan(_start, _end - _start);
        if (!framer.TryReadFrameBounds(buffered, out int payloadOffset, out int payloadLength, out int consumed))
        {
            payload = default;
            return false;
        }

        payload = buffered.Slice(payloadOffset, payloadLength);
        _start += consumed;
        if (_start == _end)
        {
            _start = 0;
            _end = 0;
        }

        return true;
    }

    private void EnsureWritable(int count)
    {
        if (_end + count <= _buffer.Length)
        {
            return;
        }

        int buffered = _end - _start;
        if (buffered + count <= _buffer.Length)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, buffered);
        }
        else
        {
            byte[] grown = new byte[Math.Max(_buffer.Length * 2, buffered + count)];
            Buffer.BlockCopy(_buffer, _start, grown, 0, buffered);
            _buffer = grown;
        }

        _start = 0;
        _end = buffered;
    }
}
