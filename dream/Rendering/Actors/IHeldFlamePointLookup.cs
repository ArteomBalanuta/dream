using OpenTK.Mathematics;

namespace Dream.Rendering.Actors;

// Where a remote player's held light burns, as the last draw placed it: the flame of the torch or lantern in the
// animated hand, in the model's own space (the caller applies the player's model matrix). False when that actor
// was not drawn this frame or holds no lit item; the caller then falls back to a fixed anchor.
public interface IHeldFlamePointLookup
{
    bool TryGetHeldFlameModelPoint(int actorId, out Vector3 modelPoint);
}
