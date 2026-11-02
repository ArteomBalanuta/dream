using System.Globalization;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
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

// One of the twelve equipment slots. Item is the snapshot's item, if any.
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

    // InventoryHudAction.Target of an Unequip that names no cell: the server takes the first fit. The
    // default Target of 0 is a real cell (the top-left one), so every Unequip says which it means.
    public const int FirstFitCell = -1;

    // The most anchors MoveTargets lists for the F3 panel's "Move To" menu.
    public const int MaxMoveTargets = 16;

    // Held-back rows (200..215) are items the server could not place on the board. The windows do not
    // draw them; they only show how many there are.
    public const int HeldBackFirstSlot = 200;
    public const int HeldBackSlots = 16;

    public static string SlotLabel(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.MainHand => "Main Hand",
        EquipmentSlot.OffHand => "Off Hand",
        EquipmentSlot.Head => "Head",
        EquipmentSlot.Body => "Body",
        EquipmentSlot.Hands => "Hands",
        EquipmentSlot.Feet => "Feet",
        EquipmentSlot.Legs => "Legs",
        EquipmentSlot.Neck => "Neck",
        EquipmentSlot.RingLeft => "Left Ring",
        EquipmentSlot.RingRight => "Right Ring",
        EquipmentSlot.Back => "Back",
        EquipmentSlot.Companion => "Companion",
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
            // Armor has no base value; it mitigates the damage the player takes (PlayerCombatant.MitigateDamage).
            IntRow(CharacterStat.Armor, "Armor", 0, status.Armor, ArmorNote)
        ];
    }

    // Unspent stat points, from the level and base stats the server sent. The same rule the server
    // checks a spend against (PlayerStatRules), so the panel never offers what would be refused.
    public static int PointsAvailable(in HudStatusSnapshot status) =>
        PlayerStatRules.PointsAvailable(status.Level, status.BaseStrength, status.BaseAttackSpeed, status.BaseVitality);

    // True when the stat can take a point now: one is free and the stat is below its cap. Armor has
    // no base value and never takes one.
    public static bool CanSpend(in HudStatusSnapshot status, CharacterStat stat)
    {
        if (PointsAvailable(status) <= 0)
        {
            return false;
        }

        return stat switch
        {
            CharacterStat.Strength => status.BaseStrength < PlayerCombatStats.MaxStrength,
            CharacterStat.Vitality => status.BaseVitality < PlayerCombatStats.MaxVitality,
            CharacterStat.AttackSpeed => status.BaseAttackSpeed + PlayerStatRules.AttackSpeedPerPoint
                <= PlayerCombatStats.MaxAttackSpeed + 0.0001f,
            _ => false
        };
    }

    // The action a spend button sends: one point on the stat. Null for a stat that takes none.
    public static InventoryHudAction? SpendAction(CharacterStat stat)
    {
        StatId? id = stat switch
        {
            CharacterStat.Strength => StatId.Strength,
            CharacterStat.AttackSpeed => StatId.AttackSpeed,
            CharacterStat.Vitality => StatId.Vitality,
            _ => null
        };
        return id is StatId spendable
            ? new InventoryHudAction(InventoryHudActionKind.SpendStat, (int)spendable, 1)
            : null;
    }

    // Always the twelve slots in slot order. A slot the snapshot does not fill, or fills with a bad
    // index, is empty. EquippedSlot is the main hand; Equipment holds 101..111, and an entry
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

    // The equipment slots an item may go to, in slot order. Bits outside the twelve slots are ignored.
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

    // What follows the count on the held-back line, shared by both inventory surfaces.
    public static string HeldBackSuffix(int count) =>
        count == 1 ? " item held back: make room" : " items held back: make room";

    // True when the EquipmentSlots mask lets an item occupy the slot. Allocation free.
    public static bool CanEquipTo(int equipSlotMask, EquipmentSlot slot) =>
        (equipSlotMask & EquipmentSlots.MaskOf(slot)) != 0;

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
                StatId.Agility => "Agility",
                StatId.Vitality => "Vitality",
                StatId.Energy => "Energy",
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

    // The context menu of a bag item: Equip to each allowed slot, Unequip when it is held, and
    // Destroy (behind a confirm) when it is not. backpackSlotIndex is the item's anchor cell (0..63).
    // Move targets come from MoveTargets.
    public static List<CharacterMenuEntry> BackpackMenu(
        in PlayerInventorySnapshot inventory,
        int backpackSlotIndex,
        in InventoryItemSnapshot item)
    {
        List<CharacterMenuEntry> entries = new();
        if (backpackSlotIndex < 0 || backpackSlotIndex >= BagGrid.CellCount)
        {
            return entries;
        }

        if (TryGetUse(item.ItemKind, out _))
        {
            // Sent as an ItemUseIntent naming the anchor and the item.
            entries.Add(new CharacterMenuEntry(
                "Use",
                new InventoryHudAction(InventoryHudActionKind.Use, backpackSlotIndex, 0, item.ItemInstanceId),
                RequiresConfirm: false));
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
                new InventoryHudAction(InventoryHudActionKind.Unequip, heldIn, FirstFitCell),
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

    // The use an item kind has (a consumable), from the shipped catalog.
    public static bool TryGetUse(string? itemKind, out ItemUseDefinition? use)
    {
        use = null;
        if (itemKind is null || !ContentCatalog.Shipped.TryGetItem(itemKind, out ItemDefinition? definition))
        {
            return false;
        }

        use = definition.Use;
        return use != null;
    }

    // The consumable that restores the pool at the lowest bag anchor (what Q and R use), and how many
    // such consumables the bag holds. Allocation free.
    public static bool TryFindConsumable(in PlayerInventorySnapshot inventory, ConsumablePool pool, out int anchor, out string itemKind, out int count)
    {
        anchor = -1;
        itemKind = string.Empty;
        count = 0;
        // Indexed, not foreach: the status bar calls this every frame and an interface enumerator allocates.
        IReadOnlyList<InventorySlotSnapshot> slots = inventory.Slots;
        for (int i = 0; i < slots.Count; i++)
        {
            InventorySlotSnapshot slot = slots[i];
            if (slot.Item is not InventoryItemSnapshot item
                || slot.SlotIndex < 0
                || slot.SlotIndex >= BagGrid.CellCount
                || !TryGetUse(item.ItemKind, out ItemUseDefinition? use)
                || (pool == ConsumablePool.Mana ? use!.RestoreMana : use!.RestoreHealth) <= 0)
            {
                continue;
            }

            if (anchor < 0 || slot.SlotIndex < anchor)
            {
                anchor = slot.SlotIndex;
                itemKind = item.ItemKind;
            }

            count++;
        }

        return anchor >= 0;
    }

    // The id of the item anchored at a bag cell (0..63).
    public static bool TryGetBagItemId(in PlayerInventorySnapshot inventory, int anchor, out long itemInstanceId)
    {
        itemInstanceId = 0;
        IReadOnlyList<InventorySlotSnapshot> slots = inventory.Slots;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].SlotIndex == anchor && slots[i].Item is InventoryItemSnapshot item)
            {
                itemInstanceId = item.ItemInstanceId;
                return true;
            }
        }

        return false;
    }

    // The context menu of an equipment slot: Unequip.
    public static List<CharacterMenuEntry> EquipmentMenu(EquipmentSlot slot)
    {
        return
        [
            new CharacterMenuEntry(
                "Unequip",
                new InventoryHudAction(InventoryHudActionKind.Unequip, EquipmentSlots.ToSlotIndex(slot), FirstFitCell),
                RequiresConfirm: false)
        ];
    }

    // The anchors the bag item at fromSlotIndex (its anchor cell) can move to without displacing another
    // item, in row-major order and at most MaxMoveTargets of them. Decided by BagGrid.PlanMove on a
    // board built from the snapshot's sparse Slots (first item wins a cell; off-board items are
    // ignored), so it agrees with the server. footprintOf is the shipped catalog unless a test passes
    // its own.
    public static List<InventoryHudAction> MoveTargets(
        in PlayerInventorySnapshot inventory,
        int fromSlotIndex,
        Func<string?, ItemFootprint>? footprintOf = null)
    {
        List<InventoryHudAction> targets = new();
        if ((uint)fromSlotIndex >= BagGrid.CellCount)
        {
            return targets;
        }

        Span<sbyte> owners = stackalloc sbyte[BagGrid.CellCount];
        Span<ItemFootprint> footprints = stackalloc ItemFootprint[BagGrid.CellCount];
        BuildBoard(inventory, footprintOf ?? BagGrid.FootprintOf, owners, footprints);
        if (owners[fromSlotIndex] != fromSlotIndex)
        {
            return targets;
        }

        for (int target = 0; target < BagGrid.CellCount && targets.Count < MaxMoveTargets; target++)
        {
            if (target != fromSlotIndex
                && BagGrid.PlanMove(owners, footprints, fromSlotIndex, target).Verdict == BagMoveVerdict.Move)
            {
                targets.Add(new InventoryHudAction(InventoryHudActionKind.Move, fromSlotIndex, target));
            }
        }

        return targets;
    }

    // Owners (anchor per cell or -1) and footprints (by anchor) of the bag a snapshot describes. A slot
    // outside 0..63, an empty slot, an item that runs off the board and an item that overlaps one
    // placed before it are skipped.
    private static void BuildBoard(
        in PlayerInventorySnapshot inventory,
        Func<string?, ItemFootprint> footprintOf,
        Span<sbyte> owners,
        Span<ItemFootprint> footprints)
    {
        owners.Fill(-1);
        footprints.Clear();
        IReadOnlyList<InventorySlotSnapshot>? slots = inventory.Slots;
        if (slots is null)
        {
            return;
        }

        ulong occupied = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            InventorySlotSnapshot slot = slots[i];
            if (slot.Item is not InventoryItemSnapshot item || item.ItemInstanceId == 0)
            {
                continue;
            }

            ItemFootprint footprint = footprintOf(item.ItemKind);
            if (!BagGrid.TryMask(slot.SlotIndex, footprint, out ulong mask) || (occupied & mask) != 0)
            {
                continue;
            }

            occupied |= mask;
            footprints[slot.SlotIndex] = footprint;
            for (int cell = 0; cell < BagGrid.CellCount; cell++)
            {
                if ((mask & (1UL << cell)) != 0)
                {
                    owners[cell] = (sbyte)slot.SlotIndex;
                }
            }
        }
    }

    // The equipment slot index (100..111) holding the item, or -1.
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
