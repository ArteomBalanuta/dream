using Dream.Client.Hover.Providers;
using Dream.Rendering.Ui;

namespace Dream.Client.Hover;

// Owns the one HoverInfo the HUD draws and the per-kind providers that fill it.
public sealed class HoverInfoComposer
{
    private readonly IHoverInfoProvider?[] _byKind;
    private readonly HoverTextCache _text = new();

    public HoverInfoComposer(IReadOnlyList<IHoverInfoProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _byKind = new IHoverInfoProvider?[Enum.GetValues<HoverKind>().Length];
        for (int n = 0; n < providers.Count; n++)
        {
            IHoverInfoProvider provider = providers[n];
            if ((int)provider.Kind > 0 && (int)provider.Kind < _byKind.Length)
            {
                _byKind[(int)provider.Kind] = provider;
            }
        }
    }

    public HoverInfo Info { get; } = new();

    // Refills Info for target from live state; true when it did. When target is gone (died, despawned) Info
    // keeps its last values with Defeated set and the health bar at 0, and this returns false. None clears Info.
    public bool Compose(HoverTargetId target)
    {
        if (target.IsNone)
        {
            Info.Clear();
            return false;
        }

        int kind = (int)target.Kind;
        if (kind > 0 && kind < _byKind.Length && _byKind[kind] is { } provider && provider.TryDescribe(target, Info))
        {
            return true;
        }

        if (Info.Target != target)
        {
            Info.Clear();
            return false;
        }

        if (!Info.Defeated)
        {
            Info.Defeated = true;
            for (int n = 0; n < Info.BarCount; n++)
            {
                HoverBar bar = Info.Bars[n];
                if (bar.Kind == HoverBarKind.Health)
                {
                    Info.Bars[n] = bar with { Current = 0, Text = _text.Bar(0, bar.Max) };
                }
            }

            // A dead thing offers nothing to do.
            Info.Hint = string.Empty;
        }

        return false;
    }
}
