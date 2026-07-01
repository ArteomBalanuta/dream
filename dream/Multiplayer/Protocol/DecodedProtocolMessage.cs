namespace Dream.Multiplayer.Protocol;

public readonly record struct DecodedProtocolMessage(
    TransportEnvelope Envelope,
    IProtocolMessage Message);
