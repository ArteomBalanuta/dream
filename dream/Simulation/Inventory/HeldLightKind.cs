namespace Dream.Simulation.Inventory;

// The light an equipped item casts. Replicated per player actor (ActorSnapshot.HeldLight, protocol v6);
// None is never written on the wire.
public enum HeldLightKind : byte
{
    None = 0,
    Torch = 1,
    Lantern = 2,
}
