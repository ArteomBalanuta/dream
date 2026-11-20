using Dream.Rendering.Ui;
using Dream.Simulation.Enemies;

namespace Dream.Client.Hover.Providers;

// Live enemies: name, level and kind, health bar, damage, attack speed and state, the attack hint.
public sealed class EnemyHoverInfoProvider : IHoverInfoProvider
{
    public EnemyHoverInfoProvider(EnemyRegistry enemies)
    {
        throw new NotImplementedException("world-hover stub");
    }

    public HoverKind Kind => HoverKind.Enemy;

    public bool TryDescribe(HoverTargetId id, HoverInfo info) => throw new NotImplementedException("world-hover stub");
}
