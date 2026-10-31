namespace Dream.Simulation.Stats;

/// <summary>
/// Base stats plus fixed modifier source slots, resolved to effective stats only when an input
/// changes. effective = clamp((base + sum(flat)) * (1 + sum(percent))), sources in ascending slot
/// order and modifiers in definition order. Resolution allocates nothing.
/// </summary>
public sealed class StatSheet
{
    public const int SourceCapacity = 16;
    public const int MaxModifiersPerSource = 8;

    private readonly StatModifier[] _modifiers = new StatModifier[SourceCapacity * MaxModifiersPerSource];
    private readonly int[] _counts = new int[SourceCapacity];
    private StatBlock _base;
    private StatBlock _effective;

    public StatSheet()
    {
        for (int i = 0; i < (int)StatId.Count; i++)
        {
            StatId stat = (StatId)i;
            _base[stat] = StatRules.ClampBase(stat, 0f);
        }

        Resolve();
    }

    public ref readonly StatBlock Base => ref _base;

    public ref readonly StatBlock Effective => ref _effective;

    public void SetBase(in StatBlock block)
    {
        for (int i = 0; i < (int)StatId.Count; i++)
        {
            StatId stat = (StatId)i;
            _base[stat] = StatRules.ClampBase(stat, block[stat]);
        }

        Resolve();
    }

    public void SetSource(int slot, ReadOnlySpan<StatModifier> modifiers)
    {
        ValidateSlot(slot);
        if (modifiers.Length > MaxModifiersPerSource)
        {
            throw new ArgumentException($"A source holds at most {MaxModifiersPerSource} modifiers.", nameof(modifiers));
        }

        for (int i = 0; i < modifiers.Length; i++)
        {
            if ((uint)modifiers[i].Stat >= (uint)StatId.Count || (uint)modifiers[i].Op > (uint)StatOp.Percent)
            {
                throw new ArgumentException("Modifier has an unknown stat or op.", nameof(modifiers));
            }

            if (!float.IsFinite(modifiers[i].Value))
            {
                throw new ArgumentException("Modifier value must be finite.", nameof(modifiers));
            }
        }

        modifiers.CopyTo(_modifiers.AsSpan(slot * MaxModifiersPerSource, MaxModifiersPerSource));
        _counts[slot] = modifiers.Length;
        Resolve();
    }

    public void ClearSource(int slot)
    {
        ValidateSlot(slot);
        if (_counts[slot] == 0)
        {
            return;
        }

        _counts[slot] = 0;
        Resolve();
    }

    private static void ValidateSlot(int slot)
    {
        if ((uint)slot >= SourceCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }
    }

    private void Resolve()
    {
        StatBlock flat = default;
        StatBlock percent = default;
        for (int slot = 0; slot < SourceCapacity; slot++)
        {
            int count = _counts[slot];
            int offset = slot * MaxModifiersPerSource;
            for (int i = 0; i < count; i++)
            {
                StatModifier modifier = _modifiers[offset + i];
                if (modifier.Op == StatOp.Flat)
                {
                    flat[modifier.Stat] += modifier.Value;
                }
                else
                {
                    percent[modifier.Stat] += modifier.Value;
                }
            }
        }

        for (int i = 0; i < (int)StatId.Count; i++)
        {
            StatId stat = (StatId)i;
            _effective[stat] = StatRules.ClampEffective(stat, (_base[stat] + flat[stat]) * (1f + percent[stat]));
        }
    }
}
