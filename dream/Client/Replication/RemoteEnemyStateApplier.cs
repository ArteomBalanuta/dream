using OpenTK.Mathematics;
using Dream.Rendering.Models;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.World;

namespace Dream.Client.Replication;


public static class RemoteEnemyStateApplier
{
    public static void ApplyActorSnapshots(EnemyRegistry registry, IReadOnlyList<ActorSnapshot> actors)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actors);

        registry.Clear();
        foreach (ActorSnapshot actor in actors)
        {
            if (actor.Kind != CreatureKind.Enemy || actor.CurrentHealth <= 0 || !actor.IsAlive)
            {
                continue;
            }

            EnemyCombatant combatant = new(
                () => actor.Position,
                actorId: actor.ActorId,
                maxHealth: actor.MaxHealth,
                attackDamage: 10,
                attackIntervalSeconds: 1f);
            combatant.Health.SetAuthoritativeState(actor.CurrentHealth, actor.MaxHealth);

            EnemyActor enemy = new(
                combatant,
                actor.Position,
                aggroRange: 10f,
                attackRange: 1.35f,
                moveSpeed: 2.8f,
                preferredStandOffDistance: WolfAssetDefinition.PreferredStandOffDistance,
                collisionRadius: WolfAssetDefinition.CollisionRadius,
                collisionHeight: WolfAssetDefinition.CollisionHeight,
                displayName: string.IsNullOrWhiteSpace(actor.DisplayName) ? "Wolf" : actor.DisplayName);
            enemy.SetFacing(actor.Facing);
            registry.Add(enemy);
        }
    }
}
