using OpenTK.Mathematics;

namespace Dream.Rendering.Actors;

// The world hover's highlight: the model under the pointer reads a little lit up. The enemy fragment shader
// multiplies the base colour by max(1 + uActorTintOffset, 0), and output is albedo x gain (up to ~2.2 near a
// brazier) with no tonemap, so the lift is a modest, slightly warm multiplier rather than an added glow.
public static class ActorHighlight
{
    // About +30% red, +24% green, +16% blue.
    public static readonly Vector3 Lift = new(0.30f, 0.24f, 0.16f);

    // The tint offset to draw an actor with: its own offset, lifted. Multiplicative, so a dark-tinted enemy
    // (the Dire Wolf's -0.45) stays darker than a plain highlighted one, and -1 (black) stays black.
    public static Vector3 Combine(Vector3 ownTint) => new(
        (1f + ownTint.X) * (1f + Lift.X) - 1f,
        (1f + ownTint.Y) * (1f + Lift.Y) - 1f,
        (1f + ownTint.Z) * (1f + Lift.Z) - 1f);

    // A ground box's colour, lifted the same way (boxes are plain albedo, there is no tint uniform), kept
    // inside the colour range.
    public static Vector3 Box(Vector3 colour) => new(
        MathF.Min(colour.X * (1f + Lift.X), 1f),
        MathF.Min(colour.Y * (1f + Lift.Y), 1f),
        MathF.Min(colour.Z * (1f + Lift.Z), 1f));

    // The enemy draw's choice: the combined tint for the highlighted actor only.
    public static Vector3 EnemyTint(int? highlightActorId, int actorId, Vector3 ownTint) =>
        highlightActorId is int id && id == actorId ? Combine(ownTint) : ownTint;

    // The villager draw's choice: the lift for the highlighted actor, zero for every other.
    public static Vector3 NpcTint(int? highlightActorId, int actorId) =>
        highlightActorId is int id && id == actorId ? Lift : Vector3.Zero;
}
