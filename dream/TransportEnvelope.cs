public readonly record struct TransportEnvelope(
    int ProtocolVersion,
    string MessageKind,
    ulong Sequence)
{
    public const int CurrentProtocolVersion = 1;
}
