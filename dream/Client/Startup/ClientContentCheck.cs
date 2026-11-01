using Dream.Simulation.Content;

namespace Dream.Client.Startup;

// The local server reads the content files (config/content) through ServerBootstrap, and a client that
// only joins a remote server does not need them (wave I). The client reads ContentCatalog.Shipped once
// at start; a failure becomes a message for Use Local Server, and a remote connect is unaffected.
public static class ClientContentCheck
{
    // Null when the content loads; otherwise the message to show.
    public static string? TryRead(Func<ContentCatalog> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            _ = read();
            return null;
        }
        catch (Exception ex) when (ex is ContentValidationException or IOException or UnauthorizedAccessException)
        {
            return $"The game content is invalid or missing: {ex.Message} Reinstall the game or restore config/content.";
        }
    }

    public static string? TryReadShipped() => TryRead(static () => ContentCatalog.Shipped);
}
