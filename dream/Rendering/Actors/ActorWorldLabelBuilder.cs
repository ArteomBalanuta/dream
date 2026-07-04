using OpenTK.Mathematics;
using Dream.Client.Presentation;
using Dream.Simulation.Enemies;
using Dream.Simulation.World;

namespace Dream.Rendering.Actors;


public static class ActorWorldLabelBuilder
{
    private static readonly Vector3 PlayerLabelOffset = new(0f, 1.35f, 0f);

    public static IReadOnlyList<WorldActorLabelState> CreateRemotePlayerLabels(
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        List<WorldActorLabelState> labels = [];
        foreach (RemotePlayerPresentationState remotePlayer in remotePlayers)
        {
            labels.Add(new WorldActorLabelState(
                remotePlayer.Pose.Position + PlayerLabelOffset,
                remotePlayer.DisplayName,
                FormatHealthLabel(remotePlayer.CurrentHealth, remotePlayer.MaxHealth)));
        }

        return labels;
    }

    public static IReadOnlyList<WorldActorLabelState> CreateEnemyLabels(IReadOnlyList<EnemyActor> enemies)
    {
        List<WorldActorLabelState> labels = [];
        foreach (EnemyActor enemy in enemies)
        {
            if (!enemy.IsActive)
            {
                continue;
            }

            labels.Add(new WorldActorLabelState(
                enemy.Position + new Vector3(0f, enemy.CollisionHeight + 0.25f, 0f),
                enemy.DisplayName,
                FormatHealthLabel(enemy.Combatant.Health.CurrentHealth, enemy.Combatant.Health.MaxHealth)));
        }

        return labels;
    }

    public static string FormatHealthLabel(int currentHealth, int maxHealth)
    {
        return $"{currentHealth}/{maxHealth}";
    }
}
