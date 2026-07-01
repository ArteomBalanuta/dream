using Dream.Simulation.World;

namespace Dream.Multiplayer.Protocol;

public sealed class ClientRoutingContext
{
    public ServerHelloMessage? LastServerHello { get; set; }

    public JoinAcceptedMessage? LastJoinAccepted { get; set; }

    public JoinRejectedMessage? LastJoinRejected { get; set; }

    public ServerSnapshotEnvelope? LastSnapshot { get; set; }

    public AuthoritativeActionResult? LastActionResult { get; set; }
}
