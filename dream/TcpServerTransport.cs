using System.Net;
using System.Net.Sockets;

public sealed class TcpServerTransport : IServerTransport
{
    private readonly TcpListener _listener;
    private bool _started;

    public TcpServerTransport(IPAddress address, int port)
    {
        _listener = new TcpListener(address, port);
        Port = port;
    }

    public int Port { get; private set; }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _listener.Start();
        _started = true;
        if (_listener.LocalEndpoint is IPEndPoint boundEndpoint)
        {
            Port = boundEndpoint.Port;
        }
    }

    public async ValueTask<ITransportConnection> AcceptClientAsync(CancellationToken cancellationToken = default)
    {
        TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
        return new TcpClientTransport(client);
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
