namespace Dream.Client.Startup;

public enum ConnectionFlowState
{
    WaitingForLocalServer,
    ReadyToConnect,
    Connecting,
    Connected,
    Failed
}
