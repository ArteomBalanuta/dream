using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Combat;

namespace Dream.Tests.Simulation.Combat;


public class AuthoritativeCombatTargetResolverTests
{
    [Fact]
    public void TryResolve_ReturnsNearestVisibleCandidate_ByPriorityAndDistance()
    {
        CombatActorHandle nearEnemy = CreateActor(10, CreatureKind.Enemy, new Vector3(3f, 0f, 0f));
        CombatActorHandle farPlayer = CreateActor(11, CreatureKind.Player, new Vector3(6f, 0f, 0f));

        CombatActorHandle? resolved = AuthoritativeCombatTargetResolver.TryResolve(
            origin: Vector3.Zero,
            direction: Vector3.UnitX,
            maxHitDistance: 10f,
            [
                new CombatTargetCandidate(farPlayer, farPlayer.GetWorldAnchor(), Priority: 20),
                new CombatTargetCandidate(nearEnemy, nearEnemy.GetWorldAnchor(), Priority: 10)
            ]);

        Assert.Equal(nearEnemy, resolved);
    }

    [Fact]
    public void TryResolve_SkipsDeadTargets()
    {
        CombatActorHandle deadEnemy = CreateActor(10, CreatureKind.Enemy, new Vector3(3f, 0f, 0f), currentHealth: 0);
        CombatActorHandle alivePlayer = CreateActor(11, CreatureKind.Player, new Vector3(6f, 0f, 0f));

        CombatActorHandle? resolved = AuthoritativeCombatTargetResolver.TryResolve(
            origin: Vector3.Zero,
            direction: Vector3.UnitX,
            maxHitDistance: 10f,
            [
                new CombatTargetCandidate(deadEnemy, deadEnemy.GetWorldAnchor(), Priority: 10),
                new CombatTargetCandidate(alivePlayer, alivePlayer.GetWorldAnchor(), Priority: 20)
            ]);

        Assert.Equal(alivePlayer, resolved);
    }

    private static CombatActorHandle CreateActor(
        int actorId,
        CreatureKind kind,
        Vector3 anchorPosition,
        int currentHealth = 100,
        int maxHealth = 100)
    {
        FakeDamageable damageable = new(currentHealth, maxHealth);
        FakeAnchorProvider anchorProvider = new(anchorPosition);
        return new CombatActorHandle(actorId, kind, damageable, anchorProvider);
    }

    private sealed class FakeDamageable : IDamageable
    {
        public FakeDamageable(int currentHealth, int maxHealth)
        {
            Health = new HealthState(maxHealth);
            Health.SetAuthoritativeState(currentHealth, maxHealth);
        }

        public HealthState Health { get; }

        public DamageReceipt ApplyDamage(int amount) => Health.ApplyDamage(amount);
    }

    private sealed class FakeAnchorProvider : ICombatAnchorProvider
    {
        private readonly Vector3 _anchor;

        public FakeAnchorProvider(Vector3 anchor)
        {
            _anchor = anchor;
        }

        public Vector3 GetCombatAnchorWorldPosition() => _anchor;
    }
}
