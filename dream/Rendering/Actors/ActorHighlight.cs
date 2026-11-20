using Dream.Rendering.Ui;
using OpenTK.Mathematics;

namespace Dream.Rendering.Actors;

// The world hover's highlight: the model under the pointer reads a little lit up. The enemy fragment shader
// multiplies the base colour by max(1 + uActorTintOffset, 0), and output is albedo x gain (up to ~2.2 near a
// brazier) with no tonemap, so the lift is a modest, slightly warm multiplier rather than an added glow.
public static class ActorHighlight
{
    // About +30% red, +24% green, +16% blue.
    public static readonly Vector3 Lift = new(0.30f, 0.24f, 0.16f);

    // The rim light (uActorRim in enemyFragment.glsl), added to the lit colour before exposure, so it lifts a
    // near-black model the multiplicative Lift cannot. Warm bone/gold, brightest where the surface turns edge-on
    // to the eye (pow 2.5). The shader caps it to the headroom under the clip point (1 / exposure minus the
    // fragment's own peak), so it reads as a gold edge at noon and at night and never clips beside a brazier.
    public static readonly Vector3 Rim = new(0.60f, 0.46f, 0.22f);

    // The rim uniform to draw an actor with: Rim for the highlighted id, zero (off) for every other.
    public static Vector4 RimFor(int? highlightId, int actorId) =>
        highlightId is int id && id == actorId ? new Vector4(Rim.X, Rim.Y, Rim.Z, 0f) : Vector4.Zero;

    // The tint offset to draw an actor with: its own offset, lifted. Multiplicative, so a dark-tinted enemy
    // (the Dire Wolf's -0.45) stays darker than a plain highlighted one, and -1 (black) stays black.
    public static Vector3 Combine(Vector3 ownTint) => new(
        (1f + ownTint.X) * (1f + Lift.X) - 1f,
        (1f + ownTint.Y) * (1f + Lift.Y) - 1f,
        (1f + ownTint.Z) * (1f + Lift.Z) - 1f);

    // A ground box's colour, lifted the same way (boxes are plain albedo, there is no tint uniform). The box
    // colours sit at or below WorldEntityRenderer.AlbedoCeiling, so the lifted one stays a transient emphasis
    // (at most AlbedoCeiling x (1 + Lift.X)) rather than a blown-out white.
    public static Vector3 Box(Vector3 colour) => new(
        colour.X * (1f + Lift.X),
        colour.Y * (1f + Lift.Y),
        colour.Z * (1f + Lift.Z));

    // Which enemy, villager and ground item draw lifted for a hover target: only the one of its kind, and none
    // for an actor id that does not fit an int (actor ids are ints; ground items keep their long id).
    public static HighlightTargets Targets(HoverTargetId current)
    {
        bool fitsInt = current.Id is >= int.MinValue and <= int.MaxValue;
        return new HighlightTargets(
            current.Kind == HoverKind.Enemy && fitsInt ? (int)current.Id : null,
            current.Kind == HoverKind.Npc && fitsInt ? (int)current.Id : null,
            current.Kind == HoverKind.GroundItem ? current.Id : null);
    }

    // The enemy draw's choice: the combined tint for the highlighted actor only.
    public static Vector3 EnemyTint(int? highlightActorId, int actorId, Vector3 ownTint) =>
        highlightActorId is int id && id == actorId ? Combine(ownTint) : ownTint;

    // The villager draw's choice: the lift for the highlighted actor, zero for every other.
    public static Vector3 NpcTint(int? highlightActorId, int actorId) =>
        highlightActorId is int id && id == actorId ? Lift : Vector3.Zero;
}

// The ids ActorHighlight.Targets picks for the enemy, villager and ground item renderers.
public readonly record struct HighlightTargets(int? EnemyActorId, int? NpcActorId, long? GroundItemId);
