namespace Dream.Multiplayer.Protocol;

public sealed record AuthoritativeActionResult(
    string SessionId,
    ulong ActionSequence,
    ulong ServerTick,
    bool Accepted,
    IActionResultPayload? Payload,
    ClientCorrectionDelta? Correction = null,
    RejectedUseDelta? Rejection = null);
