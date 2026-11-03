namespace Dream.Multiplayer.Protocol;

// Shared by both ends, like FrameLimitOptions. The server (ServerBootstrap.CreateRuntimeHost) reads the
// variable to allow noclip, freeze and the stat sliders; the client (PlayScene) reads it to predict
// noclip and freeze. It lives here so the client need not reference Dream.Server for the name.
public static class DevCommandsOptions
{
    // "1" turns dev commands on. Off by default; the integrated server turns them on itself.
    public const string EnvironmentVariable = "DREAM_DEV_COMMANDS";
}
