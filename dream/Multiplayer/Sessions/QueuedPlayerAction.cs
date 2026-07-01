using Dream.Multiplayer.Protocol;

namespace Dream.Multiplayer.Sessions;

public readonly record struct QueuedPlayerAction(
    ulong ReceiveOrder,
    PlayerSessionId SessionId,
    PlayerActionMessage Message);
