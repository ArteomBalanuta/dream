namespace Dream.Application;

// Forwards text to an expensive setter only when it differs from the last value written.
public sealed class ChangedTextSink
{
    private readonly Action<string> _apply;
    private string? _last;

    public ChangedTextSink(Action<string> apply)
    {
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    public int ApplyCount { get; private set; }

    public void Set(string text)
    {
        if (string.Equals(_last, text, StringComparison.Ordinal))
        {
            return;
        }

        _last = text;
        ApplyCount++;
        _apply(text);
    }
}
