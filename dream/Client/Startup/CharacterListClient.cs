using Dream.Client.Networking;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;

namespace Dream.Client.Startup;

// M1-C. Where the connection screen gets the account's characters for the picker.
public interface ICharacterListSource
{
    // The characters of the account the options' token belongs to; empty when the server has none,
    // keeps no accounts, or does not answer in time. Never throws for a network failure: the connect
    // that follows reports it.
    Task<IReadOnlyList<CharacterListEntry>> FetchAsync(RemoteClientOptions options, CancellationToken cancellationToken = default);
}

// Asks the server for the account's characters on a short connection of its own, before the real one
// joins: hello, the list request, the answer, then the socket closes. The token is sent once, in the
// request, and kept nowhere (it stays in the options the caller already holds).
public sealed class CharacterListClient : ICharacterListSource
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly IClientTransport _transport;
    private readonly TimeSpan _timeout;

    public CharacterListClient(IClientTransport? transport = null, TimeSpan? timeout = null)
    {
        _transport = transport ?? new TcpClientConnector();
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<IReadOnlyList<CharacterListEntry>> FetchAsync(
        RemoteClientOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.AccountToken))
        {
            return [];
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            return await ExchangeAsync(options, deadline.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NetworkDiagnosticsLog.Write("client.character-list.failed", $"endpoint={options.EndpointLabel} {ex.GetType().Name}");
            return [];
        }
    }

    private async Task<IReadOnlyList<CharacterListEntry>> ExchangeAsync(RemoteClientOptions options, CancellationToken cancellationToken)
    {
        TransportJsonCodec codec = new();
        IMessageFramer framer = FrameLimitOptions.FromEnvironment(static _ => { }).CreateClientFramer();
        await using ITransportConnection connection = await _transport.ConnectAsync(options.Host, options.Port, cancellationToken);
        using PooledFrameWriter writer = new();

        ulong sequence = 1;
        await SendAsync(new ClientHelloMessage(options.ClientName));
        await SendAsync(new ClientCharacterListRequest(options.AccountToken!));

        byte[] chunk = new byte[16 * 1024];
        FrameReceiveBuffer buffer = new();
        while (true)
        {
            int read = await connection.ReceiveAsync(chunk, cancellationToken);
            if (read <= 0)
            {
                return [];
            }

            buffer.Append(chunk.AsSpan(0, read));
            while (buffer.TryReadPayload(framer, out ReadOnlySpan<byte> payload))
            {
                if (payload.IsEmpty)
                {
                    continue;
                }

                if (codec.Decode(payload).Message is ServerCharacterList list)
                {
                    int count = Math.Min(list.Characters.Count, ServerCharacterList.MaxCharacters);
                    List<CharacterListEntry> entries = new(count);
                    for (int i = 0; i < count; i++)
                    {
                        entries.Add(list.Characters[i]);
                    }

                    return entries;
                }
            }
        }

        async ValueTask SendAsync<TMessage>(TMessage message)
            where TMessage : IClientToServerMessage
        {
            TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence++);
            writer.BeginFrame(framer);
            codec.Encode(envelope, message, writer);
            await connection.SendAsync(writer.CompleteFrame(framer), cancellationToken);
        }
    }
}
