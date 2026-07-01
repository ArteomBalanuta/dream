using Dream.Simulation.Players;

namespace Dream.Rendering.Hud;

public readonly record struct HudStatusSnapshot(
    int CurrentHealth,
    int MaxHealth,
    PlayerInteractionMode InteractionMode,
    int MinDamage,
    int MaxDamage,
    int Strength,
    float AttackSpeed,
    int Vitality)
{
    public string HealthLabel => $"HP: {CurrentHealth}/{MaxHealth}";

    public string ModeLabel => $"Mode: {InteractionMode.ToHudLabel()}";

    public string DamageLabel => $"Damage: {MinDamage}-{MaxDamage}";
}
