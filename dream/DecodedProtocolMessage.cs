public readonly record struct DecodedProtocolMessage(
    TransportEnvelope Envelope,
    IProtocolMessage Message);
