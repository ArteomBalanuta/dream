using Dream.Rendering.Models;
using Dream.Simulation.Enemies;

namespace Dream.Rendering.Actors;

public static class WolfAnimationResolver
{
    public static string ResolveClip(EnemyBehaviorState behaviorState)
    {
        return behaviorState switch
        {
            EnemyBehaviorState.Chasing => WolfAssetDefinition.MoveClip,
            EnemyBehaviorState.Attacking => WolfAssetDefinition.AttackClip,
            EnemyBehaviorState.Dead => WolfAssetDefinition.IdleClip,
            _ => WolfAssetDefinition.IdleClip
        };
    }
}
