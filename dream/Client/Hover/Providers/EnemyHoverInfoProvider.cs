using Dream.Rendering.Ui;
using Dream.Simulation.Enemies;

namespace Dream.Client.Hover.Providers;

// Live enemies: name, level, health bar, state, the attack hint. The enemy's damage and attack speed are
// placeholders on the client (RemoteEnemyStateApplier builds them), and the enemy kind is not replicated
// either, so neither is shown.
public sealed class EnemyHoverInfoProvider : IHoverInfoProvider
{
    public const string AttackHint = "[LMB] Attack";
    public const string StateLabel = "State";

    private readonly EnemyRegistry _enemies;
    private readonly HoverTextCache _text = new();

    public EnemyHoverInfoProvider(EnemyRegistry enemies)
    {
        ArgumentNullException.ThrowIfNull(enemies);
        _enemies = enemies;
    }

    public HoverKind Kind => HoverKind.Enemy;

    public bool TryDescribe(HoverTargetId id, HoverInfo info)
    {
        if (id.Kind != HoverKind.Enemy || id.Id < int.MinValue || id.Id > int.MaxValue
            || !_enemies.TryGetEnemyByActorId((int)id.Id, out EnemyActor? enemy) || enemy is null || !enemy.IsActive)
        {
            return false;
        }

        int max = Math.Max(0, enemy.Combatant.Health.MaxHealth);
        int current = Math.Clamp(enemy.Combatant.Health.CurrentHealth, 0, max);

        info.Clear();
        info.Target = id;
        info.Disposition = HoverDisposition.Hostile;
        info.Title = enemy.DisplayName;
        info.Subtitle = _text.Level(enemy.Level);
        info.BarCount = 1;
        info.Bars[0] = new HoverBar(HoverBarKind.Health, current, max, _text.Bar(current, max));
        info.LineCount = 1;
        info.Lines[0] = new HoverLine(StateLabel, StateText(enemy.BehaviorState));
        info.Hint = AttackHint;
        return true;
    }

    private static string StateText(EnemyBehaviorState state) => state switch
    {
        EnemyBehaviorState.Chasing => "Hunting",
        EnemyBehaviorState.Attacking => "Attacking",
        _ => "Idle"
    };
}
