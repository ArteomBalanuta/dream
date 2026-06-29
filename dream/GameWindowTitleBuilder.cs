public static class GameWindowTitleBuilder
{
    public const string BaseTitle = "dream";

    public static string Build(int fps, string? connectedEndpoint = null)
    {
        return string.IsNullOrWhiteSpace(connectedEndpoint)
            ? $"{BaseTitle} | FPS: {fps}"
            : $"{BaseTitle} | {connectedEndpoint} | FPS: {fps}";
    }
}
