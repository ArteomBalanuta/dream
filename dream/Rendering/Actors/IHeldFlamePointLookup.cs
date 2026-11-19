using OpenTK.Mathematics;

namespace Dream.Rendering.Actors;

// Where a remote player's held light burns, as the last draw that posed it placed it: the flame of the torch or lantern
// in the animated hand, in the model's own space (the caller applies the player's model matrix). The point stays
// available while the player is not drawn (culled, still loading) so the fire does not jump; it is withheld for an
// actor id now carrying another session, for different held items, and for a player never drawn with a lit item. The
// caller then falls back to a fixed anchor.
public interface IHeldFlamePointLookup
{
    bool TryGetHeldFlameModelPoint(int actorId, string sessionId, in PlayerEquipmentVisuals equipment, out Vector3 modelPoint);
}
