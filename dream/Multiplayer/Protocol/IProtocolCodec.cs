using System.Buffers;

namespace Dream.Multiplayer.Protocol;

public interface IProtocolCodec
{
    byte[] Encode<TMessage>(TransportEnvelope envelope, TMessage message)
        where TMessage : IProtocolMessage;

    // Writes the whole envelope, payload included, in one pass. Output is byte-identical to the
    // byte[] overload.
    void Encode<TMessage>(TransportEnvelope envelope, TMessage message, IBufferWriter<byte> destination)
        where TMessage : IProtocolMessage;

    // Writes only the payload JSON, for callers that wrap one payload in several envelopes.
    void EncodePayload<TMessage>(TMessage message, IBufferWriter<byte> destination)
        where TMessage : IProtocolMessage;

    // Writes an envelope around payload JSON produced by EncodePayload. The payload is copied as-is
    // and is not validated, and its kind cannot be checked against envelope.MessageKind.
    void WriteEnvelope(TransportEnvelope envelope, ReadOnlySpan<byte> payloadJson, IBufferWriter<byte> destination);

    DecodedProtocolMessage Decode(ReadOnlySpan<byte> payload);

    // Decode on the server's receive path. A codec that knows which kinds only the server sends
    // (TransportJsonCodec) refuses them from the envelope's kind, before it decodes the payload,
    // so a client cannot make the server build a world chunk or snapshot. Others decode as usual
    // and leave the check to the caller.
    DecodedProtocolMessage DecodeFromClient(ReadOnlySpan<byte> payload) => Decode(payload);
}
