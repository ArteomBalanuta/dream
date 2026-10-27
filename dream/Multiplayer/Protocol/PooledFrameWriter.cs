using System.Buffers;
using System.Text.Json;

namespace Dream.Multiplayer.Protocol;

// Builds one outbound frame at a time in a buffer rented from ArrayPool<byte>.Shared: BeginFrame
// reserves the framer's header, the codec writes the envelope straight after it, and
// CompleteFrame patches the length prefix in place. The memory CompleteFrame returns aliases the
// pooled buffer and is only valid until the next BeginFrame, Reset or Dispose.
//
// Single owner, not thread-safe. The writer is meant to be kept and reused; it also caches one
// Utf8JsonWriter for TransportJsonCodec, so a warmed writer encodes without allocating.
//
// Growth and shrinking: the buffer doubles as needed for big frames. Once a frame has grown it
// past RetainedCapacityLimit, the next Reset hands that buffer back to the pool and rents a
// default-size one, so a single oversized frame does not pin a large array for the owner's
// lifetime.
public sealed class PooledFrameWriter : IBufferWriter<byte>, IDisposable
{
    public const int DefaultInitialCapacity = 4 * 1024;
    public const int RetainedCapacityLimit = 64 * 1024;

    private byte[] _buffer;
    private int _written;
    private int _headerLength;
    private bool _frameOpen;
    private bool _disposed;
    private Utf8JsonWriter? _jsonWriter;
    private JsonWriterOptions _jsonWriterOptions;

    public PooledFrameWriter(int initialCapacity = DefaultInitialCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    public int WrittenCount => _written;

    public int Capacity => _buffer.Length;

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public void BeginFrame(IMessageFramer framer)
    {
        ArgumentNullException.ThrowIfNull(framer);
        Reset();
        int headerLength = framer.HeaderLength;
        EnsureCapacity(headerLength);
        _buffer.AsSpan(0, headerLength).Clear();
        _headerLength = headerLength;
        _written = headerLength;
        _frameOpen = true;
    }

    public ReadOnlyMemory<byte> CompleteFrame(IMessageFramer framer)
    {
        ArgumentNullException.ThrowIfNull(framer);
        ThrowIfDisposed();
        if (!_frameOpen)
        {
            throw new InvalidOperationException("CompleteFrame requires a frame started with BeginFrame.");
        }

        if (framer.HeaderLength != _headerLength)
        {
            throw new InvalidOperationException(
                $"Framer header length {framer.HeaderLength} does not match the {_headerLength} bytes reserved by BeginFrame.");
        }

        framer.WriteHeader(_buffer.AsSpan(0, _headerLength), _written - _headerLength);
        _frameOpen = false;
        return _buffer.AsMemory(0, _written);
    }

    public void Reset()
    {
        ThrowIfDisposed();
        if (_buffer.Length > RetainedCapacityLimit)
        {
            byte[] oversized = _buffer;
            _buffer = ArrayPool<byte>.Shared.Rent(DefaultInitialCapacity);
            ArrayPool<byte>.Shared.Return(oversized);
        }

        _written = 0;
        _headerLength = 0;
        _frameOpen = false;
    }

    public void Advance(int count)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _buffer.Length - _written)
        {
            throw new InvalidOperationException(
                $"Cannot advance {count} bytes; only {_buffer.Length - _written} bytes are available.");
        }

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DiscardJsonWriter();
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _written = 0;
        _frameOpen = false;
    }

    // Hands out the cached Utf8JsonWriter pointed at this buffer. Options are fixed per
    // Utf8JsonWriter, so a different set of options replaces the cached instance. The requested
    // options are remembered as given: Utf8JsonWriter.Options reports normalized values (a MaxDepth
    // of 0 reads back as the default depth), so comparing against it would never match.
    internal Utf8JsonWriter GetJsonWriter(JsonWriterOptions options)
    {
        ThrowIfDisposed();
        if (_jsonWriter is null || !HaveSameOptions(_jsonWriterOptions, options))
        {
            DiscardJsonWriter();
            _jsonWriter = new Utf8JsonWriter(this, options);
            _jsonWriterOptions = options;
            return _jsonWriter;
        }

        _jsonWriter.Reset(this);
        return _jsonWriter;
    }

    // Reset() drops bytes a failed encode left pending, so Dispose has nothing to flush into the buffer.
    private void DiscardJsonWriter()
    {
        if (_jsonWriter is null)
        {
            return;
        }

        _jsonWriter.Reset();
        _jsonWriter.Dispose();
        _jsonWriter = null;
    }

    // Field by field: JsonWriterOptions has no Equals override, and the default one boxes.
    private static bool HaveSameOptions(JsonWriterOptions left, JsonWriterOptions right)
    {
        return ReferenceEquals(left.Encoder, right.Encoder)
            && left.Indented == right.Indented
            && left.IndentCharacter == right.IndentCharacter
            && left.IndentSize == right.IndentSize
            && left.MaxDepth == right.MaxDepth
            && string.Equals(left.NewLine, right.NewLine, StringComparison.Ordinal)
            && left.SkipValidation == right.SkipValidation;
    }

    private void EnsureCapacity(int sizeHint)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        if (sizeHint == 0)
        {
            sizeHint = 1;
        }

        int available = _buffer.Length - _written;
        if (sizeHint <= available)
        {
            return;
        }

        long required = (long)_written + sizeHint;
        long doubled = (long)_buffer.Length * 2;
        long newSize = Math.Max(required, doubled);
        if (newSize > Array.MaxLength)
        {
            if (required > Array.MaxLength)
            {
                throw new OutOfMemoryException($"Frame would exceed the maximum array length ({required} bytes).");
            }

            newSize = Array.MaxLength;
        }

        byte[] grown = ArrayPool<byte>.Shared.Rent((int)newSize);
        _buffer.AsSpan(0, _written).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
