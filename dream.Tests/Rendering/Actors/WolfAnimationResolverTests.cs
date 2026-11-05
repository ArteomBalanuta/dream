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
// The pack's real death clip, which this used to resolve to the idle.
[InlineData(EnemyBehaviorState.Dead, WolfAssetDefinition.DieClip)]
public void ResolveClip_Maps_Behavior_State_To_Wolf_Clip(EnemyBehaviorState state, string expected)
{
    Assert.Equal(expected, WolfAnimationResolver.ResolveClip(state));
    // The behaviour-only entry point is exactly the no-measured-speed case.
    Assert.Equal(WolfAnimationResolver.ResolveClip(state, 0f), WolfAnimationResolver.ResolveClip(state));
}

// A measured chase speed is what tells the two gaits apart; nothing else changes.
[Fact]
public void ResolveClip_WithAMeasuredSpeed_PicksTheGait()
{
    Assert.Equal(WolfAssetDefinition.WalkClip, WolfAnimationResolver.ResolveClip(EnemyBehaviorState.Chasing, 1.4f));
    Assert.Equal(WolfAssetDefinition.GallopClip, WolfAnimationResolver.ResolveClip(EnemyBehaviorState.Chasing, 5.0f));
    Assert.Equal(WolfAssetDefinition.IdleClip, WolfAnimationResolver.ResolveClip(EnemyBehaviorState.Idle, 5.0f));
    Assert.Equal(WolfAssetDefinition.AttackClip, WolfAnimationResolver.ResolveClip(EnemyBehaviorState.Attacking, 5.0f));
    Assert.Equal(WolfAssetDefinition.DieClip, WolfAnimationResolver.ResolveClip(EnemyBehaviorState.Dead, 5.0f));
}
}
