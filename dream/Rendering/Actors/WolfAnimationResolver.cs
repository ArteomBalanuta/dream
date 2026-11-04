using Dream.Simulation.Enemies;

namespace Dream.Rendering.Actors;

// The wolf's clip for a behaviour state. Superseded by EnemyClipSet, which every enemy visual
// carries; this stays as the wolf's named entry point.
public static class WolfAnimationResolver
{
    public static string ResolveClip(EnemyBehaviorState behaviorState) =>
        EnemyVisualCatalog.WolfClips.Resolve(behaviorState);
}
