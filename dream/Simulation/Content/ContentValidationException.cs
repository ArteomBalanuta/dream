namespace Dream.Simulation.Content;

// A content file (items.json, enemies.json) is malformed or breaks a rule. Thrown at load, which
// is server start, so the message names the file and the JSON path.
public sealed class ContentValidationException : Exception
{
    public ContentValidationException(string message)
        : base(message)
    {
    }

    public ContentValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
