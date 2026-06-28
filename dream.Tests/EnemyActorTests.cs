using OpenTK.Mathematics;
using Xunit;

public class EnemyActorTests
{
    [Fact]
    public void Update_Transitions_To_Chasing_When_Player_Is_Inside_Aggro_Range()
    {
        EnemyActor actor = CreateActor();

        actor.Update(0.25f, new Vector3(3f, 0f, 0f));

        Assert.Equal(EnemyBehaviorState.Chasing, actor.BehaviorState);
    }

    [Fact]
    public void Update_Transitions_To_Attacking_When_Player_Is_In_Melee_Range()
    {
        EnemyActor actor = CreateActor();

        actor.Update(0.25f, new Vector3(0.5f, 0f, 0f));

        Assert.Equal(EnemyBehaviorState.Attacking, actor.BehaviorState);
    }

    [Fact]
    public void Update_Stays_Idle_When_Player_Is_Outside_Aggro_Range()
    {
        EnemyActor actor = CreateActor();

        actor.Update(0.25f, new Vector3(10f, 0f, 0f));

        Assert.Equal(EnemyBehaviorState.Idle, actor.BehaviorState);
    }

    [Fact]
    public void Update_Moves_Toward_Player_While_Chasing()
    {
        EnemyActor actor = CreateActor(moveSpeed: 4f);

        actor.Update(0.5f, new Vector3(4f, 0f, 0f));

        Assert.Equal(new Vector3(2f, 0f, 0f), actor.Position);
        Assert.Equal(Vector3.UnitX, actor.Forward);
    }

    [Fact]
    public void Update_Does_Not_Move_When_Attacking()
    {
        EnemyActor actor = CreateActor(moveSpeed: 4f);

        actor.Update(0.5f, new Vector3(0.75f, 0f, 0f));

        Assert.Equal(Vector3.Zero, actor.Position);
        Assert.Equal(EnemyBehaviorState.Attacking, actor.BehaviorState);
    }

    private static EnemyActor CreateActor(float moveSpeed = 3f)
    {
        EnemyCombatant combatant = new(() => Vector3.Zero, maxHealth: 30, attackDamage: 6, attackIntervalSeconds: 1f);
        return new EnemyActor(
            combatant,
            Vector3.Zero,
            aggroRange: 5f,
            attackRange: 1.25f,
            moveSpeed: moveSpeed);
    }
}
