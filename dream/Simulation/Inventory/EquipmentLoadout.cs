using Dream.Simulation.Stats;

namespace Dream.Simulation.Inventory;

// The modifiers of what a player has equipped, per slot, ready to hand to a StatSheet. Preallocated:
// Clear, Set and Get allocate nothing after construction. Set skips a modifier that could make the
// sheet throw (non-finite value, unknown stat or op) and ignores anything past
// StatSheet.MaxModifiersPerSource, so hostile data (a server snapshot or a stored row) can never
// throw on the tick.
public sealed class EquipmentLoadout
{
    private readonly StatModifier[] _modifiers = new StatModifier[EquipmentSlots.Count * StatSheet.MaxModifiersPerSource];
    private readonly int[] _counts = new int[EquipmentSlots.Count];

    public void Clear() => Array.Clear(_counts);

    // flatOnly also skips Percent modifiers: no shipped item gives one, so a Percent in a server
    // snapshot is hostile (a finite overflow times zero is NaN).
    public void Set(EquipmentSlot slot, IReadOnlyList<StatModifier> modifiers, bool flatOnly = false)
    {
        int index = (int)slot;
        if ((uint)index >= EquipmentSlots.Count)
        {
            return;
        }

        int count = 0;
        int offset = index * StatSheet.MaxModifiersPerSource;
        if (modifiers != null)
        {
            for (int i = 0; i < modifiers.Count && count < StatSheet.MaxModifiersPerSource; i++)
            {
                StatModifier modifier = modifiers[i];
                if ((uint)modifier.Stat >= (uint)StatId.Count
                    || (uint)modifier.Op > (uint)StatOp.Percent
                    || (flatOnly && modifier.Op != StatOp.Flat)
                    || !float.IsFinite(modifier.Value))
                {
                    continue;
                }

                _modifiers[offset + count++] = modifier;
            }
        }

        _counts[index] = count;
    }

    public ReadOnlySpan<StatModifier> Get(EquipmentSlot slot)
    {
        int index = (int)slot;
        if ((uint)index >= EquipmentSlots.Count)
        {
            return default;
        }

        return new ReadOnlySpan<StatModifier>(_modifiers, index * StatSheet.MaxModifiersPerSource, _counts[index]);
    }
}
