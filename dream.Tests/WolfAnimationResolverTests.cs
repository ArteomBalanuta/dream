using Xunit;

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
