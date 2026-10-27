namespace Dream.Multiplayer.Protocol;

public readonly record struct TransportEnvelope(
    int ProtocolVersion,
    string MessageKind,
    ulong Sequence)
{
    // Version 2 leaves null properties and derived properties out of payloads. Decode rejects any
    // other version.
    public const int CurrentProtocolVersion = 2;
}
