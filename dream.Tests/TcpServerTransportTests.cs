using System.Net;
using Xunit;

public class TcpServerTransportTests
{
    [Fact]
    public async Task Start_With_Port_Zero_Exposes_Resolved_Bound_Port()
    {
        await using TcpServerTransport transport = new(IPAddress.Loopback, 0);

        transport.Start();

        Assert.InRange(transport.Port, 1, 65535);
    }
}
