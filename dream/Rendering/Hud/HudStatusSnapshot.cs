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
    int Vitality,
    int Level,
    int Experience,
    int ExperienceToNextLevel)
{
    public string HealthLabel => $"HP: {CurrentHealth}/{MaxHealth}";

    public string ModeLabel => $"Mode: {InteractionMode.ToHudLabel()}";

    public string DamageLabel => $"Damage: {MinDamage}-{MaxDamage}";

    public string LevelLabel => $"LVL: {Level}";

    public string ExperiencePercentLabel => $"EXPERIENCE: {ExperiencePercent}%";

    public string ExperienceProgressLabel => $"{Experience}/{ExperienceToNextLevel}";

    public int ExperiencePercent => ExperienceToNextLevel <= 0
        ? 0
        : (int)MathF.Round((Experience / (float)ExperienceToNextLevel) * 100f, MidpointRounding.AwayFromZero);
}
