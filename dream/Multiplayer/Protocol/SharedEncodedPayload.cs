using System.Runtime.ExceptionServices;
using Dream.Multiplayer.Diagnostics;

namespace Dream.Multiplayer.Protocol;

// The payload JSON of one server snapshot, encoded at most once and shared by every connection it
// is broadcast to. Only the envelope (its sequence) differs per connection, so each connection's
// writer wraps these bytes with IProtocolCodec.WriteEnvelope instead of serializing the snapshot
// again.
//
// Encoding is lazy: the first writer task that needs the bytes encodes them, off the tick thread,
// and writers that arrive meanwhile wait for it. A failed encode is logged once and cached, so
// every connection sharing the payload sees the same exception and faults.
//
// The bytes are a plain array, not a pooled one: they are read by several writer tasks whose
// lifetimes (coalescing, faults, disposal) are independent, so there is no single point at which
// they could be handed back to a pool. The array is read-only once published.
public sealed class SharedEncodedPayload
{
    [ThreadStatic]
    private static PooledFrameWriter? t_scratch;

    private const int StateNew = 0;
    private const int StateEncoding = 1;
    private const int StateDone = 2;

    private readonly IProtocolCodec _codec;

    // Published (volatile) before _state becomes StateDone; null while unencoded or after a fault.
    private volatile byte[]? _bytes;
    private Exception? _fault;
    private int _state;

    public SharedEncodedPayload(IProtocolCodec codec, ServerSnapshotMessage message)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(message);
        _codec = codec;
        Message = message;
    }

    public ServerSnapshotMessage Message { get; }

    public bool IsEncoded => _bytes is not null;

    // Encodes on first use; rethrows the cached exception (original stack preserved) if that
    // encode failed.
    public ReadOnlySpan<byte> Bytes => GetBytes();

    // Once-gate: the caller that wins the CompareExchange encodes; the others spin briefly and
    // then wait on the instance monitor. No delegate or event is allocated.
    private byte[] GetBytes()
    {
        byte[]? bytes = _bytes;
        if (bytes is not null)
        {
            return bytes;
        }

        if (Interlocked.CompareExchange(ref _state, StateEncoding, StateNew) == StateNew)
        {
            try
            {
                _bytes = EncodeOnce();
            }
            catch (Exception ex)
            {
                _fault = ex;
            }
            finally
            {
                Volatile.Write(ref _state, StateDone);
                lock (this)
                {
                    Monitor.PulseAll(this);
                }
            }
        }
        else
        {
            WaitUntilDone();
        }

        return _bytes ?? Rethrow();
    }

    private void WaitUntilDone()
    {
        SpinWait spin = default;
        while (Volatile.Read(ref _state) != StateDone && !spin.NextSpinWillYield)
        {
            spin.SpinOnce();
        }

        if (Volatile.Read(ref _state) == StateDone)
        {
            return;
        }

        // The completer sets StateDone before taking the lock to pulse, so checking the state
        // while holding the lock cannot miss the pulse.
        lock (this)
        {
            while (Volatile.Read(ref _state) != StateDone)
            {
                Monitor.Wait(this);
            }
        }
    }

    private byte[] Rethrow()
    {
        ExceptionDispatchInfo.Throw(_fault!);
        return null!;
    }

    private byte[] EncodeOnce()
    {
        // A per-thread scratch writer keeps the encode itself allocation-free once warm; only the
        // published array is allocated. The encode is synchronous, so the writer is never shared.
        PooledFrameWriter scratch = t_scratch ??= new PooledFrameWriter();
        try
        {
            scratch.Reset();
            _codec.EncodePayload(Message, scratch);
            return scratch.WrittenMemory.ToArray();
        }
        catch (Exception ex)
        {
            NetworkDiagnosticsLog.Write(
                "server.snapshot.encode.fault",
                $"tick={Message.Snapshot.ServerTick} error={ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            // Hands an oversized buffer back to the pool rather than keeping it for this thread.
            scratch.Reset();
        }
    }
}
