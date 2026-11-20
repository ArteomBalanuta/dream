using Dream.Rendering.Core;

namespace Dream.Client.Input;

// Whether the pointer rests on a live enemy: the same pick a right click uses, so a wall in front
// hides the enemy behind it. Not gated on interaction mode; the cursor shows the hover in both.
public sealed class EnemyHoverProbe
{
    private readonly IClickMoveHost _host;

    public EnemyHoverProbe(IClickMoveHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public bool Evaluate(in WorldPointerState pointer, bool allowGameplayInput, bool windowFocused)
    {
        if (!allowGameplayInput || !windowFocused || pointer.OverHud)
        {
            return false;
        }

        if (!float.IsFinite(pointer.Position.X) || !float.IsFinite(pointer.Position.Y))
        {
            return false;
        }

        return _host.IsEnemyAt(pointer.Position);
    }
}
