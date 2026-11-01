using System.Globalization;
using Dream.Simulation.Inventory;
using Dream.Simulation.Stats;

namespace Dream.Rendering.Hud;

// Colour key for an item label. The HUD maps it to an ImGui colour; keeping the key here lets a
// test pin the rule without a GL context.
public enum RarityColorKey : byte
{
    Common,
    Uncommon,
    Rare
}

public enum CharacterStat : byte
{
    Strength,
    AttackSpeed,
    Vitality,
    Armor
}

// One line of the character panel's stat table: base + bonus = effective. Bonus is the effective
// value minus the base value, so a capped stat shows the capped result.
public readonly record struct CharacterStatRow(
    CharacterStat Stat,
    string Label,
    float Base,
    float Bonus,
    float Effective,
    string BaseText,
    string BonusText,
    string EffectiveText,
    string? Note);

// One of the four equipment slots. Item is the snapshot's item, if any.
public readonly record struct CharacterSlotRow(
    EquipmentSlot Slot,
    int SlotIndex,
    string SlotLabel,
    InventoryItemSnapshot? Item,
    string ItemLabel,
    RarityColorKey Color);

// An entry of an item's context menu. Picking it sends exactly Action; a RequiresConfirm entry
// asks first.
public readonly record struct CharacterMenuEntry(string Label, InventoryHudAction Action, bool RequiresConfirm);

// What the character panel and the item context menu show, built from the server's snapshots
// only. Pure: no ImGui and no GL. Every input is hostile: indices, masks, enums and lists are
// range-checked, and nothing here throws on a bad snapshot.
public static class CharacterPanelModel
{
    public const string ArmorNote = "reduces damage taken";
    public const int MaxTooltipLines = 8;

    public static string SlotLabel(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.MainHand => "Main Hand",
        EquipmentSlot.OffHand => "Off Hand",
        EquipmentSlot.Head => "Head",
        EquipmentSlot.Body => "Body",
        _ => "Slot"
    };

    public static RarityColorKey ColorKey(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Uncommon => RarityColorKey.Uncommon,
        ItemRarity.Rare => RarityColorKey.Rare,
        _ => RarityColorKey.Common
    };

    // Strength, Attack Speed, Vitality and Armor, in that order. Agility, Energy and Movement Speed
    // are not live stats and are not shown.
    public static CharacterStatRow[] BuildStatRows(in HudStatusSnapshot status)
    {
        return
        [
            IntRow(CharacterStat.Strength, "Strength", status.BaseStrength, status.Strength, null),
            AttackSpeedRow(status.BaseAttackSpeed, status.AttackSpeed),
            IntRow(CharacterStat.Vitality, "Vitality", status.BaseVitality, status.Vitality, null),
            // Armor has no base value and, in wave H, no effect on combat.
            IntRow(CharacterStat.Armor, "Armor", 0, status.Armor, ArmorNote)
        ];
    }

    // Always the four slots in slot order. A slot the snapshot does not fill, or fills with a bad
    // index, is empty. EquippedSlot is the main hand; Equipment holds 101..103, and an entry
    // naming 100 or anything else is ignored.
    public static CharacterSlotRow[] BuildSlotRows(PlayerInventorySnapshot? inventory)
    {
        CharacterSlotRow[] rows = new CharacterSlotRow[EquipmentSlots.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            EquipmentSlot slot = (EquipmentSlot)i;
            rows[i] = MakeSlotRow(slot, null);
        }

        if (inventory is not PlayerInventorySnapshot snapshot)
        {
            return rows;
        }

        if (snapshot.EquippedSlot?.Item is InventoryItemSnapshot mainHand)
        {
            rows[(int)EquipmentSlot.MainHand] = MakeSlotRow(EquipmentSlot.MainHand, mainHand);
        }

        IReadOnlyList<InventorySlotSnapshot>? equipment = snapshot.Equipment;
        if (equipment is not null)
        {
            for (int i = 0; i < equipment.Count; i++)
            {
                InventorySlotSnapshot entry = equipment[i];
                if (entry.Item is InventoryItemSnapshot item
                    && EquipmentSlots.TryFromSlotIndex(entry.SlotIndex, out EquipmentSlot slot)
                    && slot != EquipmentSlot.MainHand)
                {
                    rows[(int)slot] = MakeSlotRow(slot, item);
                }
            }
        }

        return rows;
    }

    // The equipment slots an item may go to, in slot order. Bits outside the four slots are ignored.
    public static List<EquipmentSlot> AllowedSlots(int equipSlotMask)
    {
        List<EquipmentSlot> slots = new(EquipmentSlots.Count);
        for (int i = 0; i < EquipmentSlots.Count; i++)
        {
            EquipmentSlot slot = (EquipmentSlot)i;
            if ((equipSlotMask & EquipmentSlots.MaskOf(slot)) != 0)
            {
                slots.Add(slot);
            }
        }

        return slots;
    }

    // Lines for a tooltip, from a rolled item's own modifiers (an unrolled item's lines are its
    // DetailLines). Unknown stats, percent modifiers and non-finite values are skipped.
    public static List<string> TooltipLines(in InventoryItemSnapshot item)
    {
        List<string> lines = new();
        if (!item.IsRolled)
        {
            return lines;
        }

        lines.Add(RarityLabel(item.Rarity));
        IReadOnlyList<StatModifier>? modifiers = item.Modifiers;
        if (modifiers is null)
        {
            return lines;
        }

        for (int i = 0; i < modifiers.Count && lines.Count <= MaxTooltipLines; i++)
        {
            StatModifier modifier = modifiers[i];
            if (modifier.Op != StatOp.Flat || !float.IsFinite(modifier.Value))
            {
                continue;
            }

            string? name = modifier.Stat switch
            {
                StatId.Strength => "Strength",
                StatId.AttackSpeed => "Attack Speed",
                StatId.Vitality => "Vitality",
                StatId.Armor => "Armor",
                _ => null
            };
            if (name is null)
            {
                continue;
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{Signed(modifier.Value)} {name}"));
        }

        return lines;
    }

    public static string RarityLabel(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Uncommon => "Uncommon",
        ItemRarity.Rare => "Rare",
        _ => "Common"
    };

    // The context menu of a backpack item: Equip to each allowed slot, Unequip when it is held, and
    // Destroy (behind a confirm) when it is not. Move targets come from MoveTargets.
    public static List<CharacterMenuEntry> BackpackMenu(
        in PlayerInventorySnapshot inventory,
        int backpackSlotIndex,
        in InventoryItemSnapshot item)
    {
        List<CharacterMenuEntry> entries = new();
        if (backpackSlotIndex < 0 || backpackSlotIndex >= InventoryConstants.BackpackSlotCount)
        {
            return entries;
        }

        foreach (EquipmentSlot slot in AllowedSlots(item.EquipSlots))
        {
            entries.Add(new CharacterMenuEntry(
                $"Equip: {SlotLabel(slot)}",
                new InventoryHudAction(InventoryHudActionKind.EquipTo, backpackSlotIndex, EquipmentSlots.ToSlotIndex(slot)),
                RequiresConfirm: false));
        }

        int heldIn = FindEquipmentSlotHolding(inventory, item.ItemInstanceId);
        if (heldIn >= 0)
        {
            entries.Add(new CharacterMenuEntry(
                "Unequip",
                new InventoryHudAction(InventoryHudActionKind.Unequip, heldIn),
                RequiresConfirm: false));
        }
        else
        {
            // The server refuses to destroy an equipped item, so the menu does not offer it.
            entries.Add(new CharacterMenuEntry(
                "Destroy...",
                new InventoryHudAction(InventoryHudActionKind.Destroy, backpackSlotIndex, 0, item.ItemInstanceId),
                RequiresConfirm: true));
        }

        return entries;
    }

    // The context menu of an equipment slot: Unequip.
    public static List<CharacterMenuEntry> EquipmentMenu(EquipmentSlot slot)
    {
        return
        [
            new CharacterMenuEntry(
                "Unequip",
                new InventoryHudAction(InventoryHudActionKind.Unequip, EquipmentSlots.ToSlotIndex(slot)),
                RequiresConfirm: false)
        ];
    }

    // The backpack slots an item may be moved to: every slot of the grid except its own. The grid
    // size is the snapshot's, clamped to the 16 backpack slots, so a hostile size lists no bad index.
    public static List<InventoryHudAction> MoveTargets(in PlayerInventorySnapshot inventory, int fromSlotIndex)
    {
        List<InventoryHudAction> targets = new();
        if (fromSlotIndex < 0 || fromSlotIndex >= InventoryConstants.BackpackSlotCount)
        {
            return targets;
        }

        long gridSize = (long)Math.Max(inventory.Columns, 0) * Math.Max(inventory.Rows, 0);
        int count = (int)Math.Min(gridSize, InventoryConstants.BackpackSlotCount);
        for (int target = 0; target < count; target++)
        {
            if (target != fromSlotIndex)
            {
                targets.Add(new InventoryHudAction(InventoryHudActionKind.Move, fromSlotIndex, target));
            }
        }

        return targets;
    }

    // The equipment slot index (100..103) holding the item, or -1.
    public static int FindEquipmentSlotHolding(in PlayerInventorySnapshot inventory, long itemInstanceId)
    {
        CharacterSlotRow[] rows = BuildSlotRows(inventory);
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Item is InventoryItemSnapshot held && held.ItemInstanceId == itemInstanceId)
            {
                return rows[i].SlotIndex;
            }
        }

        return -1;
    }

    private static CharacterSlotRow MakeSlotRow(EquipmentSlot slot, InventoryItemSnapshot? item)
    {
        string label = item is InventoryItemSnapshot held
            ? (string.IsNullOrEmpty(held.DisplayName) ? "(unnamed)" : held.DisplayName)
            : "Empty";
        RarityColorKey color = item is InventoryItemSnapshot rolled ? ColorKey(rolled.Rarity) : RarityColorKey.Common;
        return new CharacterSlotRow(slot, EquipmentSlots.ToSlotIndex(slot), SlotLabel(slot), item, label, color);
    }

    private static CharacterStatRow IntRow(CharacterStat stat, string label, int baseValue, int effective, string? note)
    {
        int bonus = effective - baseValue;
        return new CharacterStatRow(
            stat,
            label,
            baseValue,
            bonus,
            effective,
            baseValue.ToString(CultureInfo.InvariantCulture),
            bonus == 0 ? string.Empty : Signed(bonus),
            effective.ToString(CultureInfo.InvariantCulture),
            note);
    }

    private static CharacterStatRow AttackSpeedRow(float baseValue, float effective)
    {
        // Two decimals are enough: the stat moves in steps of 0.05, and float noise stays out of
        // the sum.
        float bonus = MathF.Round(effective - baseValue, 2);
        return new CharacterStatRow(
            CharacterStat.AttackSpeed,
            "Attack Speed",
            baseValue,
            bonus,
            effective,
            Format(baseValue),
            bonus == 0f ? string.Empty : Signed(bonus),
            Format(effective),
            null);
    }

    private static string Format(float value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Signed(float value) =>
        value >= 0f
            ? "+" + value.ToString("0.##", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
}
