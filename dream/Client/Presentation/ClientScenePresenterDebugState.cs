namespace Dream.Client.Presentation;

public sealed record ClientScenePresenterDebugState(
    string? LocalSessionId,
    int RosterCount,
    IReadOnlyList<string> RosterSessions,
    int RemoteEntryCount,
    IReadOnlyList<string> RemoteSessions,
    int SampledRemoteCount);
