using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Simulation.World;

namespace Dream.Server;

public sealed class ServerConnection : IAsyncDisposable
{
    private readonly ITransportConnection _transport;
    private readonly DedicatedServerHost _host;
    private readonly IProtocolCodec _codec;
    private readonly IMessageFramer _framer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _receiveChunk = new byte[16 * 1024];
    private readonly List<byte> _receiveBuffer = [];
    private ulong _nextOutboundSequence = 1;

    public ServerConnection(
        ITransportConnection transport,
        DedicatedServerHost host,
        IProtocolCodec codec,
        IMessageFramer framer)
    {
        _transport = transport;
        _host = host;
        _codec = codec;
        _framer = framer;
    }

    public string ConnectionId => _transport.ConnectionId;

    public async ValueTask<bool> ProcessIncomingAsync(CancellationToken cancellationToken = default)
    {
        int bytesRead = await _transport.ReceiveAsync(_receiveChunk, cancellationToken);
        if (bytesRead <= 0)
        {
            return false;
        }

        _receiveBuffer.AddRange(_receiveChunk.AsSpan(0, bytesRead).ToArray());
        await FlushInboundFramesAsync(cancellationToken);
        return true;
    }

    public ValueTask SendSnapshotAsync(ServerSnapshotMessage snapshotMessage, CancellationToken cancellationToken = default)
    {
        return SendMessageAsync(snapshotMessage, cancellationToken);
    }

    public ValueTask SendActionAsync(ServerActionMessage actionMessage, CancellationToken cancellationToken = default)
    {
        return SendMessageAsync(actionMessage, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _sendLock.Dispose();
        return _transport.DisposeAsync();
    }

    private async ValueTask FlushInboundFramesAsync(CancellationToken cancellationToken)
    {
        while (_framer.TryDeframe(_receiveBuffer.ToArray(), out byte[]? payload, out int bytesConsumed))
        {
            _receiveBuffer.RemoveRange(0, bytesConsumed);
            if (payload == null)
            {
                continue;
            }

            DecodedProtocolMessage decoded = _codec.Decode(payload);
            IClientToServerMessage clientMessage = decoded.Message as IClientToServerMessage
                ?? throw new InvalidOperationException(
                    $"Received non-client message kind '{decoded.Message.MessageKind}' on server connection '{ConnectionId}'.");
            IReadOnlyList<IServerToClientMessage> replies = _host.HandleClientMessage(ConnectionId, clientMessage);
            foreach (IServerToClientMessage reply in replies)
            {
                await SendMessageAsync(reply, cancellationToken);
            }
        }
    }

    private async ValueTask SendMessageAsync(IServerToClientMessage message, CancellationToken cancellationToken)
    {
        byte[] payload = EncodeServerMessage(message);
        byte[] framed = _framer.Frame(payload);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _transport.SendAsync(framed, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private byte[] EncodeServerMessage(IServerToClientMessage message)
    {
        TransportEnvelope envelope = new(
            TransportEnvelope.CurrentProtocolVersion,
            message.MessageKind,
            _nextOutboundSequence++);

        return message switch
        {
            ServerHelloMessage serverHello => _codec.Encode(envelope, serverHello),
            JoinAcceptedMessage joinAccepted => _codec.Encode(envelope, joinAccepted),
            JoinRejectedMessage joinRejected => _codec.Encode(envelope, joinRejected),
            ServerSnapshotMessage snapshot => _codec.Encode(envelope, snapshot),
            ServerActionMessage action => _codec.Encode(envelope, action),
            _ => throw new InvalidOperationException(
                $"Unsupported server message type '{message.GetType().Name}' for connection encoding.")
        };
    }
}
