using Dream.Multiplayer.Protocol;

namespace Dream.Rendering.Hud;

// The classic HUD's way to ask for an action (M5 and M6). The HUD draws on the render path, so it cannot
// send anything itself: a click enqueues the payload here, and the scene drains the queue once a frame and
// sends each as a player action. Bounded: a frame of clicks past MaxPending is dropped. One instance per
// scene, created with a parameterless constructor (the headless play scene fills fields by it).
public sealed class HudIntentQueue
{
    public const int MaxPending = 16;

    private readonly Queue<IPlayerActionPayload> _pending = new();

    public int Count => _pending.Count;

    public void Enqueue(IPlayerActionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (_pending.Count < MaxPending)
        {
            _pending.Enqueue(payload);
        }
    }

    public bool TryDequeue(out IPlayerActionPayload payload)
    {
        if (_pending.Count == 0)
        {
            payload = null!;
            return false;
        }

        payload = _pending.Dequeue();
        return true;
    }
}
