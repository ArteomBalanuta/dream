namespace Dream.Multiplayer.Diagnostics;

public sealed record AuthoritativeFlowTraceEntry(
    string Category,
    string SessionId,
    ulong? Sequence,
    ulong ServerTick,
    bool Accepted,
    bool CorrectionApplied,
    string Detail);
