using System.Globalization;
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
        AppendRemotePlayerLabels(remotePlayers, labels, healthLabels: null);
        return labels;
    }

    public static IReadOnlyList<WorldActorLabelState> CreateEnemyLabels(IReadOnlyList<EnemyActor> enemies)
    {
        List<WorldActorLabelState> labels = [];
        AppendEnemyLabels(enemies, labels, healthLabels: null);
        return labels;
    }

    // Adds to a caller-owned list; with a cache, health text is reused until the values change.
    public static void AppendRemotePlayerLabels(
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers,
        List<WorldActorLabelState> destination,
        ActorHealthLabelCache? healthLabels)
    {
        for (int index = 0; index < remotePlayers.Count; index++)
        {
            RemotePlayerPresentationState remotePlayer = remotePlayers[index];
            destination.Add(new WorldActorLabelState(
                remotePlayer.Pose.Position + PlayerLabelOffset,
                remotePlayer.DisplayName,
                GetHealthLabel(healthLabels, remotePlayer.CurrentHealth, remotePlayer.MaxHealth)));
        }
    }

    // With a pose source, each label sits over the enemy's interpolated render pose for renderTick
    // (the pose the wolf model draws at); without one, or when the source has no pose for an enemy,
    // it sits over the authoritative position.
    public static void AppendEnemyLabels(
        IReadOnlyList<EnemyActor> enemies,
        List<WorldActorLabelState> destination,
        ActorHealthLabelCache? healthLabels,
        IEnemyRenderPoseSource? poseSource = null,
        double renderTick = 0d)
    {
        for (int index = 0; index < enemies.Count; index++)
        {
            EnemyActor enemy = enemies[index];
            if (!enemy.IsActive)
            {
                continue;
            }

            Vector3 position = EnemyRenderer.ResolveRenderPose(enemy, poseSource, renderTick).Position;
            destination.Add(new WorldActorLabelState(
                position + new Vector3(0f, enemy.CollisionHeight + 0.25f, 0f),
                enemy.DisplayName,
                GetHealthLabel(healthLabels, enemy.Combatant.Health.CurrentHealth, enemy.Combatant.Health.MaxHealth)));
        }
    }

    public static string FormatHealthLabel(int currentHealth, int maxHealth)
    {
        return $"{currentHealth}/{maxHealth}";
    }

    private static string GetHealthLabel(ActorHealthLabelCache? healthLabels, int currentHealth, int maxHealth)
    {
        return healthLabels is null
            ? FormatHealthLabel(currentHealth, maxHealth)
            : healthLabels.Get(currentHealth, maxHealth);
    }
}

// Health text keyed by (current, max). An actor's label is built once and reused until its
// current or max health changes. Owned by one render thread; not thread-safe.
public sealed class ActorHealthLabelCache
{
    public const int MaxEntries = 4096;

    private readonly Dictionary<(int Current, int Max), string> _labels = new();
    private CultureInfo? _culture;

    public int Count => _labels.Count;

    public string Get(int currentHealth, int maxHealth)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        if (!ReferenceEquals(_culture, culture))
        {
            _labels.Clear();
            _culture = culture;
        }

        if (_labels.TryGetValue((currentHealth, maxHealth), out string? label))
        {
            return label;
        }

        if (_labels.Count >= MaxEntries)
        {
            _labels.Clear();
        }

        label = ActorWorldLabelBuilder.FormatHealthLabel(currentHealth, maxHealth);
        _labels.Add((currentHealth, maxHealth), label);
        return label;
    }
}
