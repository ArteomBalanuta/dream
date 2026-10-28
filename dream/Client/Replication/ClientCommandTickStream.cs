using Dream.Client.Networking;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Client.Replication;

// Turns per-frame input into exactly one command per client prediction tick (NETR-4).
//
// The client used to number a command per render frame and send one only on ticks where a fresh
// frame had arrived: about 30 commands per second at 30 fps, with sequence gaps at 144 fps, and a
// sequence that meant "frame" on the client but "tick" on the server.
//
// Now every render frame only captures input, which merges into a pending input (newest axes and
// facing; one-shot presses kept until a tick consumes them). Each fixed 64 Hz tick then produces
// one command whose sequence is that prediction tick: the pending input if a frame captured one
// since the last tick, otherwise the last input held with its one-shots cleared. A frame faster
// than 64 Hz produces no tick and so no command; a slower frame runs one tick, and one command,
// per elapsed tick, up to SimulationClock's per-advance cap so a stall cannot spiral.
//
// Sequences are consecutive and monotonic, so both the server's latest-only inbox and a buffered
// one that consumes one command per tick accept them.
//
// Send stalls mirror the server's NETR-3 rule. A server that gets no command repeats the last one
// for MaxCommandRepeatTicks (BufferedPlayerCommandInbox.ServerRepeatTicks) ticks and then idles, and when the backlog arrives
// its inbox skips the commands those starved ticks already simulated (BufferedPlayerCommandInbox).
// Once the oldest command the client has not handed to the transport is that many ticks old, the
// server is idling this player, so every further tick of the stall predicts AND sends no movement
// (facing, presses and primary use are kept): the client's prediction is then what the server
// simulated, and the backlog's idle commands match the server's idle ticks. Before, the client
// walked through the stall and snapped back about 4.5 m when it ended. The backlog is read from
// the probe given to ObserveSendBacklog (RemoteClientSession.PendingCommandCount: commands queued
// but not yet handed to the transport; a send blocked by the stall holds the oldest one).
public sealed class ClientCommandTickStream
{
    // Queued commands at which the oldest unsent one (one more, held by the blocked send) is
    // MaxCommandRepeatTicks old when the next command is produced.
    public const int SendStallBacklog = BufferedPlayerCommandInbox.ServerRepeatTicks - 1;

    private readonly ServerTickRunner _tickRunner;
    private readonly PlayerSessionId _fallbackSessionId;
    private Func<int>? _unsentCommandCount;
    private PlayerCommandFrame _pendingInput;
    private bool _hasPendingInput;
    private PlayerCommandFrame _heldInput;
    private bool _hasHeldInput;

    public ClientCommandTickStream(
        PlayerSessionId fallbackSessionId,
        int maxTicksPerAdvance = SimulationClock.DefaultMaxTicksPerAdvance)
    {
        _fallbackSessionId = fallbackSessionId;
        _tickRunner = new ServerTickRunner(maxTicksPerAdvance);
    }

    // The newest prediction tick, which is also the newest command sequence.
    public ulong LastTick => _tickRunner.LastCompletedTick;

    // Time accumulated toward the next tick, for render interpolation.
    public double RemainingSeconds => _tickRunner.RemainingSeconds;

    // Ticks skipped because one advance would have exceeded the cap.
    public long DroppedTicks => _tickRunner.DroppedTicks;

    // Ticks whose command was made idle because the send was stalled past the repeat window.
    public long SendStallIdleTicks { get; private set; }

    // Gives the stream the number of commands queued but not yet handed to the transport, read
    // once per tick (no allocation). Null turns send-stall idling off.
    public void ObserveSendBacklog(Func<int>? unsentCommandCount)
    {
        _unsentCommandCount = unsentCommandCount;
    }

    // Merges one render frame's input into the input the next tick consumes. Its sequence is
    // ignored; the tick assigns it.
    public void CaptureInput(PlayerCommandFrame input)
    {
        _pendingInput = _hasPendingInput
            ? ClientOutboundCommandQueue.Merge(_pendingInput, input)
            : input;
        _hasPendingInput = true;
    }

    // Runs every elapsed prediction tick, each with its one command (Sequence == tick). Callers
    // pass a static lambda plus explicit state so a tick allocates no closure.
    public int Advance<TState>(
        double elapsedSeconds,
        TState state,
        Action<TState, PlayerCommandFrame, float> tickAction)
    {
        ArgumentNullException.ThrowIfNull(tickAction);

        return _tickRunner.Advance(
            elapsedSeconds,
            (Stream: this, State: state, Action: tickAction),
            static (context, tick, fixedDeltaSeconds) =>
                context.Action(context.State, context.Stream.NextCommand(tick), fixedDeltaSeconds));
    }

    private PlayerCommandFrame NextCommand(ulong tick)
    {
        PlayerCommandFrame source;
        if (_hasPendingInput)
        {
            source = _pendingInput;
            _hasPendingInput = false;
        }
        else if (_hasHeldInput)
        {
            source = _heldInput;
        }
        else
        {
            source = PlayerCommandFrame.CreateIdle(_fallbackSessionId, 0);
        }

        _heldInput = HoldWithoutOneShots(source);
        _hasHeldInput = true;
        PlayerCommandFrame command = source with { Sequence = tick };
        if (_unsentCommandCount != null && _unsentCommandCount() >= SendStallBacklog)
        {
            // The server idles this player past its repeat window; predict and send the same.
            PlayerMoveIntent move = command.MoveIntent;
            command = command with
            {
                MoveIntent = new PlayerMoveIntent(0f, 0f, 0f, move.JumpPressed, move.FreezePressed, move.ToggleNoclipPressed)
            };
            SendStallIdleTicks++;
        }

        return command;
    }

    // What a tick without fresh input repeats: the held axes, facing and primary use, without the
    // presses (jump, freeze, noclip toggle, mode request) or look deltas the source already used.
    private static PlayerCommandFrame HoldWithoutOneShots(PlayerCommandFrame frame)
    {
        PlayerMoveIntent move = frame.MoveIntent;
        return frame with
        {
            RequestedMode = PlayerModeSwitchIntent.None,
            MoveIntent = new PlayerMoveIntent(move.ForwardAxis, move.StrafeAxis, move.VerticalAxis),
            LookIntent = new PlayerLookIntent(0f, 0f, frame.LookIntent.Facing)
        };
    }
}
