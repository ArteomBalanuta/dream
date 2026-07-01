using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;
using Dream.Simulation.Enemies;

namespace Dream.Tests.Rendering.Actors;


public class WolfAnimationResolverTests
{
[Theory]
[InlineData(EnemyBehaviorState.Idle, WolfAssetDefinition.IdleClip)]
[InlineData(EnemyBehaviorState.Chasing, WolfAssetDefinition.MoveClip)]
[InlineData(EnemyBehaviorState.Attacking, WolfAssetDefinition.AttackClip)]
public void ResolveClip_Maps_Behavior_State_To_Wolf_Clip(EnemyBehaviorState state, string expected)
{
    Assert.Equal(expected, WolfAnimationResolver.ResolveClip(state));
}
}
