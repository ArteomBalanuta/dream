namespace Dream.Multiplayer.Diagnostics;

public sealed class AuthoritativeFlowTrace
{
    private readonly int _maxEntries;

    public AuthoritativeFlowTrace(int maxEntries = 64)
    {
        _maxEntries = Math.Max(1, maxEntries);
    }

    public List<AuthoritativeFlowTraceEntry> Entries { get; } = [];

    public void Add(AuthoritativeFlowTraceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entries.Add(entry);
        if (Entries.Count > _maxEntries)
        {
            Entries.RemoveAt(0);
        }
    }
}
