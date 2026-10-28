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
//
// SCALE-2: the bytes are the shared broadcast body (ServerSnapshotMessage.SharedBody): no
// lastAcceptedCommandSequences, which each connection writes into its own envelope header
// (TransportEnvelope.Ack), and actors as the export's delta frame. A connection that does not hold
// that frame's base gets KeyframeBytes instead: the same body with the frame as a keyframe,
// encoded at most once as well, and only when some connection needs it.
public sealed class SharedEncodedPayload
{
    [ThreadStatic]
    private static PooledFrameWriter? t_scratch;

    private const int StateNew = 0;
    private const int StateEncoding = 1;
    private const int StateDone = 2;

    // Private gate: a lock on the public instance could be contended by outside code. Created
    // only by a waiter or by the first request for the keyframe twin, so the common (uncontended,
    // no twin) path allocates nothing for it. It also holds the twin, so the payload object keeps
    // its size (the gate-overhead budget of SharedEncodedPayloadTests).
    private object? _gate;
    private readonly IProtocolCodec _codec;

    // Published (volatile) before _state becomes StateDone; null while unencoded or after a fault.
    private volatile byte[]? _bytes;
    private ExceptionDispatchInfo? _fault;
    private int _state;

    public SharedEncodedPayload(IProtocolCodec codec, ServerSnapshotMessage message)
        : this(codec, message, keyframeVariant: false)
    {
    }

    private SharedEncodedPayload(IProtocolCodec codec, ServerSnapshotMessage message, bool keyframeVariant)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(message);
        _codec = codec;
        Message = message;
        if (keyframeVariant)
        {
            _gate = new Companion(isKeyframeVariant: true);
        }
    }

    public ServerSnapshotMessage Message { get; }

    public bool IsEncoded => _bytes is not null;

    private bool IsKeyframeVariant => (Volatile.Read(ref _gate) as Companion)?.IsKeyframeVariant ?? false;

    // True when the actors are a delta frame, so a connection without its base needs the
    // keyframe twin.
    public bool HasDeltaActors => !IsKeyframeVariant && Message.Snapshot.ActorDelta is { IsKeyframe: false };

    // The payload whose actors are a keyframe of the same frame; this payload itself when its
    // actors already are one (or are the legacy full list).
    public SharedEncodedPayload KeyframePayload
    {
        get
        {
            if (!HasDeltaActors)
            {
                return this;
            }

            Companion companion = GetOrCreateGate();
            SharedEncodedPayload? keyframe = Volatile.Read(ref companion.Keyframe);
            if (keyframe != null)
            {
                return keyframe;
            }

            Interlocked.CompareExchange(ref companion.Keyframe, new SharedEncodedPayload(_codec, Message, keyframeVariant: true), null);
            return companion.Keyframe!;
        }
    }

    public bool IsKeyframeEncoded => (Volatile.Read(ref _gate) as Companion)?.Keyframe?.IsEncoded ?? false;

    public ReadOnlySpan<byte> KeyframeBytes => KeyframePayload.Bytes;

    // Encodes on first use; rethrows the cached exception (original stack preserved) if that
    // encode failed.
    public ReadOnlySpan<byte> Bytes => GetBytes();

    // Once-gate: the caller that wins the CompareExchange encodes; the others spin briefly and
    // then wait on the private gate monitor. No delegate or event is allocated.
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
                _fault = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                // Full fence (Exchange) so the state write is visible before _gate is read; a waiter
                // publishes _gate with another full fence before it re-checks the state.
                Interlocked.Exchange(ref _state, StateDone);
                object? gate = Volatile.Read(ref _gate);
                if (gate is not null)
                {
                    lock (gate)
                    {
                        Monitor.PulseAll(gate);
                    }
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

        // The gate is published before the state is re-checked and the completer sets StateDone
        // before reading the gate, so either the completer sees the gate and pulses under the lock,
        // or the waiter sees StateDone; the pulse cannot be missed.
        Companion gate = GetOrCreateGate();
        lock (gate)
        {
            while (Volatile.Read(ref _state) != StateDone)
            {
                Monitor.Wait(gate);
            }
        }
    }

    private Companion GetOrCreateGate()
    {
        return (Companion)(Volatile.Read(ref _gate) ?? Interlocked.CompareExchange(ref _gate, new Companion(isKeyframeVariant: false), null) ?? _gate!);
    }

    // The gate monitor, and the keyframe twin of a payload whose actors are a delta frame.
    private sealed class Companion
    {
        public SharedEncodedPayload? Keyframe;

        public Companion(bool isKeyframeVariant)
        {
            IsKeyframeVariant = isKeyframeVariant;
        }

        public bool IsKeyframeVariant { get; }
    }

    private byte[] Rethrow()
    {
        // Captured once when the fault happened; Throw() on the shared info rethrows the same
        // exception instance without readers racing to re-capture its stack trace.
        _fault!.Throw();
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
            SharedSnapshotEncodeScope.Enter(Message, IsKeyframeVariant);
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
            SharedSnapshotEncodeScope.Exit();
        }
    }
}

// How the snapshot being encoded on this thread is to be written, for the encode SharedEncodedPayload
// runs: as the shared broadcast body (no sequence map), and optionally with its actors as the
// frame's keyframe twin. Read by ServerSnapshotMessageJsonConverter for the one top-level snapshot
// message it writes. A thread-static scope rather than a copy of the message (or a wrapper message
// type), so the shared encode allocates nothing beyond the published array. Encodes are
// synchronous, so the scope never leaks to another encode.
internal static class SharedSnapshotEncodeScope
{
    [ThreadStatic]
    private static ServerSnapshotMessage? t_message;

    [ThreadStatic]
    private static bool t_keyframe;

    public static void Enter(ServerSnapshotMessage message, bool keyframe)
    {
        t_message = message;
        t_keyframe = keyframe;
    }

    public static void Exit()
    {
        t_message = null;
        t_keyframe = false;
    }

    // True when message is the one this thread's shared encode is writing.
    public static bool Applies(ServerSnapshotMessage message, out bool keyframe)
    {
        if (ReferenceEquals(t_message, message))
        {
            keyframe = t_keyframe;
            return true;
        }

        keyframe = false;
        return false;
    }
}
