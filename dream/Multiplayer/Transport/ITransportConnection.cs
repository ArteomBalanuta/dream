using System.Threading;
using System.Threading.Tasks;

namespace Dream.Multiplayer.Transport;


public interface ITransportConnection : IAsyncDisposable
{
    string ConnectionId { get; }

    ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}
