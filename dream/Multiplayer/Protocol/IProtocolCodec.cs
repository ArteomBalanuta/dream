namespace Dream.Multiplayer.Protocol;

public interface IProtocolCodec
{
    byte[] Encode<TMessage>(TransportEnvelope envelope, TMessage message)
        where TMessage : IProtocolMessage;

    DecodedProtocolMessage Decode(ReadOnlySpan<byte> payload);
}
