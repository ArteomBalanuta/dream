using System.Globalization;

namespace Dream.Client.Hover.Providers;

// Strings the hover providers show, built once per distinct value and reused while the values hold, so a
// hovered thing allocates nothing per frame. Bounded: a cache over its cap is cleared and refilled, like
// ActorHealthLabelCache. Invariant culture, so a culture change needs no flush. Not thread-safe.
public sealed class HoverTextCache
{
    public const int MaxEntries = 1024;

    private readonly Dictionary<(int Current, int Max), string> _bars = new();
    private readonly Dictionary<int, string> _levels = new();

    public int Count => _bars.Count + _levels.Count;

    // "80 / 100". Values are shown as given; the caller clamps them.
    public string Bar(int current, int max)
    {
        (int, int) key = (current, max);
        if (_bars.TryGetValue(key, out string? text))
        {
            return text;
        }

        if (_bars.Count >= MaxEntries)
        {
            _bars.Clear();
        }

        text = string.Create(CultureInfo.InvariantCulture, $"{current} / {max}");
        _bars.Add(key, text);
        return text;
    }

    // "Lv 7". The level is clamped to [1, ClientScenePresenter.MaxDisplayLevel] first (wire values are hostile).
    public string Level(int level)
    {
        level = Math.Clamp(level, 1, Dream.Client.Presentation.ClientScenePresenter.MaxDisplayLevel);
        if (_levels.TryGetValue(level, out string? text))
        {
            return text;
        }

        if (_levels.Count >= MaxEntries)
        {
            _levels.Clear();
        }

        text = string.Create(CultureInfo.InvariantCulture, $"Lv {level}");
        _levels.Add(level, text);
        return text;
    }
}
