namespace Dream.Multiplayer.Protocol;

public sealed record RejectedUseDelta(string Reason, string? Detail = null);
