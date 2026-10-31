using System.Globalization;
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
    int ExperienceToNextLevel,
    int BaseStrength = 0,
    float BaseAttackSpeed = 0f,
    int BaseVitality = 0,
    int Armor = 0)
{
    // The HUD reads these every frame, so each label is rebuilt only when its values change.
    private static readonly LastLabelCache<(int Current, int Max)> HealthLabels =
        new(static key => $"HP: {key.Current}/{key.Max}");

    private static readonly LastLabelCache<PlayerInteractionMode> ModeLabels =
        new(static mode => $"Mode: {mode.ToHudLabel()}");

    private static readonly LastLabelCache<(int Min, int Max)> DamageLabels =
        new(static key => $"Damage: {key.Min}-{key.Max}");

    private static readonly LastLabelCache<int> LevelLabels =
        new(static level => $"LVL: {level}");

    private static readonly LastLabelCache<int> ExperiencePercentLabels =
        new(static percent => $"EXPERIENCE: {percent}%");

    private static readonly LastLabelCache<(int Experience, int ToNextLevel)> ExperienceProgressLabels =
        new(static key => $"{key.Experience}/{key.ToNextLevel}");

    public string HealthLabel => HealthLabels.Get((CurrentHealth, MaxHealth));

    public string ModeLabel => ModeLabels.Get(InteractionMode);

    public string DamageLabel => DamageLabels.Get((MinDamage, MaxDamage));

    public string LevelLabel => LevelLabels.Get(Level);

    public string ExperiencePercentLabel => ExperiencePercentLabels.Get(ExperiencePercent);

    public string ExperienceProgressLabel => ExperienceProgressLabels.Get((Experience, ExperienceToNextLevel));

    public int ExperiencePercent => ExperienceToNextLevel <= 0
        ? 0
        : (int)MathF.Round((Experience / (float)ExperienceToNextLevel) * 100f, MidpointRounding.AwayFromZero);

    // Remembers the label for the last key and culture, so the text matches $"" exactly. The entry
    // is swapped as one reference, so a reader on another thread never sees a mixed pair.
    private sealed class LastLabelCache<TKey>(Func<TKey, string> format)
    {
        private Entry? _last;

        public string Get(TKey key)
        {
            Entry? last = _last;
            CultureInfo culture = CultureInfo.CurrentCulture;
            if (last is not null &&
                ReferenceEquals(last.Culture, culture) &&
                EqualityComparer<TKey>.Default.Equals(last.Key, key))
            {
                return last.Label;
            }

            Entry next = new(key, culture, format(key));
            _last = next;
            return next.Label;
        }

        private sealed record Entry(TKey Key, CultureInfo Culture, string Label);
    }
}
