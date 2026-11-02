using Dream.Multiplayer.Protocol;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;

namespace Dream.Rendering.Hud;

// What the quickbar shows and remembers (M4): the six bound entries, the count of each bound item kind
// in the bag, the local player's mana and the running cooldowns. One instance per scene, created with a
// parameterless constructor (the headless play scene fills fields by it). The scene feeds it from the
// inventory snapshot and ServerPlayerVitals; the panel only reads it. The server is the authority for
// every number here, so wire data is clamped and unknown content is shown as an empty tile.
public sealed class QuickbarHudState
{
    public const int SlotCount = InventoryConstants.QuickbarSlots;
    public const int MaxManaCap = 100_000;
    public const int MaxKeyChars = 64;

    private readonly QuickbarSlotSnapshot[] _slots = new QuickbarSlotSnapshot[SlotCount];
    private readonly int[] _itemCounts = new int[SlotCount];
    private readonly int[] _itemBagSlots = new int[SlotCount];
    private readonly long[] _itemIds = new long[SlotCount];
    private readonly int[] _manaCosts = new int[SlotCount];
    private readonly int[] _cooldownTotals = new int[SlotCount];
    private readonly bool[] _healOnly = new bool[SlotCount];
    private readonly string[] _labels = new string[SlotCount];
    private readonly string[] _iconNames = new string[SlotCount];
    private readonly AbilityCooldown[] _cooldowns = new AbilityCooldown[ServerPlayerVitals.MaxCooldowns];
    private int _cooldownCount;
    private IReadOnlyList<QuickbarSlotSnapshot>? _bound;
    private PlayerInventorySnapshot? _inventory;
    private bool _hasData;

    public QuickbarHudState()
    {
        Clear();
    }

    // The server tick the cooldown overlays count down to; the scene sets it every frame from its
    // estimate of the server clock.
    public ulong NowTick { get; set; }

    public int CurrentMana { get; private set; }

    public int MaxMana { get; private set; }

    // A click on a tile left this slot to be cycled (0..5), or -1. Taken by the scene.
    public int CycleRequest { get; private set; } = -1;

    // True once the join or a snapshot gave this state something; false after Clear.
    public bool HasData => _hasData;

    public int CooldownCount => _cooldownCount;

    // Empties everything: the content default bar, no mana, no cooldowns, no bag.
    public void Clear()
    {
        CurrentMana = 0;
        MaxMana = 0;
        _cooldownCount = 0;
        Array.Clear(_cooldowns);
        CycleRequest = -1;
        _bound = null;
        _inventory = null;
        _hasData = false;
        Resolve();
    }

    // The owner's mana and running cooldowns. Wire data is hostile: max is 0..MaxManaCap, current is
    // 0..max, at most MaxCooldowns entries are kept and an entry without a key is dropped.
    public void ApplyVitals(ServerPlayerVitals? vitals)
    {
        if (vitals is null)
        {
            return;
        }

        _hasData = true;
        MaxMana = Math.Clamp(vitals.MaxMana, 0, MaxManaCap);
        CurrentMana = Math.Clamp(vitals.CurrentMana, 0, MaxMana);
        _cooldownCount = 0;
        IReadOnlyList<AbilityCooldown>? list = vitals.Cooldowns;
        int seen = Math.Min(list?.Count ?? 0, ServerPlayerVitals.MaxCooldowns);
        for (int i = 0; i < seen; i++)
        {
            AbilityCooldown entry = list![i];
            if (string.IsNullOrEmpty(entry.Key) || entry.Key.Length > MaxKeyChars)
            {
                continue;
            }

            _cooldowns[_cooldownCount++] = entry;
        }
    }

    // The inventory snapshot: its quickbar (null shows the content default) and the bag, which gives
    // the count of each bound item kind. Bag anchors only: held-back rows and worn items do not count.
    public void ApplyInventory(PlayerInventorySnapshot? inventory)
    {
        _hasData = true;
        _inventory = inventory;
        _bound = inventory?.Quickbar;
        Resolve();
    }

    public QuickbarSlotSnapshot Slot(int index) =>
        (uint)index < SlotCount ? _slots[index] : new QuickbarSlotSnapshot(index, QuickbarEntryKind.None, string.Empty);

    public bool IsEmpty(int index) => Slot(index).Kind == QuickbarEntryKind.None;

    // The short name the tile falls back to without art: the skill's or item's display name.
    public string LabelOf(int index) => (uint)index < SlotCount ? _labels[index] : string.Empty;

    // The sprite name the tile looks for first: the skill's iconKey, or "item.<kind>". Empty for an
    // empty slot. Built when the bar is resolved, so the panel allocates nothing per frame.
    public string IconNameOf(int index) => (uint)index < SlotCount ? _iconNames[index] : string.Empty;

    // Items of the bound kind in the bag; 0 for a skill or an empty slot.
    public int ItemCount(int index) => (uint)index < SlotCount ? _itemCounts[index] : 0;

    public int ManaCostOf(int index) => (uint)index < SlotCount ? _manaCosts[index] : 0;

    public bool IsHeal(int index) => (uint)index < SlotCount && _healOnly[index];

    // The bag item an Item slot would use: the lowest anchor holding the bound kind.
    public bool TryGetItemTarget(int index, out int bagSlot, out long itemInstanceId)
    {
        bagSlot = 0;
        itemInstanceId = 0;
        if ((uint)index >= SlotCount || _slots[index].Kind != QuickbarEntryKind.Item || _itemIds[index] == 0)
        {
            return false;
        }

        bagSlot = _itemBagSlots[index];
        itemInstanceId = _itemIds[index];
        return true;
    }

    // True when the tile's entry costs more mana than the player has, or is an item with none left.
    public bool IsUnavailable(int index)
    {
        if ((uint)index >= SlotCount)
        {
            return false;
        }

        return _slots[index].Kind switch
        {
            QuickbarEntryKind.Skill => _manaCosts[index] > CurrentMana,
            QuickbarEntryKind.Item => _itemCounts[index] <= 0,
            _ => false
        };
    }

    // Ticks until the entry of this slot is ready, 0 when it is not cooling down. total is the entry's
    // full cooldown (for the overlay fraction), 0 when unknown.
    public ulong RemainingTicks(int index, ulong nowTick, out int total)
    {
        total = 0;
        if ((uint)index >= SlotCount || _slots[index].Kind == QuickbarEntryKind.None)
        {
            return 0;
        }

        string key = _slots[index].Key;
        for (int i = 0; i < _cooldownCount; i++)
        {
            if (string.Equals(_cooldowns[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                total = _cooldownTotals[index];
                return _cooldowns[i].ReadyTick > nowTick ? _cooldowns[i].ReadyTick - nowTick : 0;
            }
        }

        return 0;
    }

    // A click on a tile (inventory open) asks to cycle it.
    public void RequestCycle(int index)
    {
        if ((uint)index < SlotCount)
        {
            CycleRequest = index;
        }
    }

    public int TakeCycleRequest()
    {
        int slot = CycleRequest;
        CycleRequest = -1;
        return slot;
    }

    // The entry a click moves this slot to: skills in content order, then the consumable kinds in the
    // bag in anchor order, then None, then the first skill again. An entry that is not in the list
    // (an item kind no longer in the bag) starts again at the first skill.
    public QuickbarSlotSnapshot NextEntry(int index)
    {
        List<QuickbarSlotSnapshot> cycle = [];
        IReadOnlyList<SkillDefinition> skills = ContentCatalog.Shipped.Skills;
        for (int i = 0; i < skills.Count; i++)
        {
            cycle.Add(new QuickbarSlotSnapshot(index, QuickbarEntryKind.Skill, skills[i].Key));
        }

        IReadOnlyList<InventorySlotSnapshot>? bag = _inventory?.Slots;
        for (int i = 0; bag is not null && i < bag.Count; i++)
        {
            if (!IsBagConsumable(bag[i], out InventoryItemSnapshot item))
            {
                continue;
            }

            if (!cycle.Exists(e => e.Kind == QuickbarEntryKind.Item && string.Equals(e.Key, item.ItemKind, StringComparison.OrdinalIgnoreCase)))
            {
                cycle.Add(new QuickbarSlotSnapshot(index, QuickbarEntryKind.Item, item.ItemKind));
            }
        }

        cycle.Add(new QuickbarSlotSnapshot(index, QuickbarEntryKind.None, string.Empty));

        QuickbarSlotSnapshot current = Slot(index);
        int at = cycle.FindIndex(e => e.Kind == current.Kind && string.Equals(e.Key, current.Key, StringComparison.OrdinalIgnoreCase));
        return cycle[at < 0 ? 0 : (at + 1) % cycle.Count];
    }

    private static bool IsBagConsumable(InventorySlotSnapshot slot, out InventoryItemSnapshot item)
    {
        item = default;
        if (slot.Item is not InventoryItemSnapshot found
            || found.ItemInstanceId == 0
            || slot.IsEquipped
            || (uint)slot.SlotIndex >= (uint)BagGrid.CellCount
            || string.IsNullOrEmpty(found.ItemKind)
            || !ContentCatalog.Shipped.TryGetItem(found.ItemKind, out ItemDefinition definition)
            || definition.Use is null)
        {
            return false;
        }

        item = found;
        return true;
    }

    // Rebuilds the six tiles from the bound list (or the default), then the bag counts.
    private void Resolve()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            _slots[i] = new QuickbarSlotSnapshot(i, QuickbarEntryKind.None, string.Empty);
            _itemCounts[i] = 0;
            _itemBagSlots[i] = 0;
            _itemIds[i] = 0;
            _manaCosts[i] = 0;
            _cooldownTotals[i] = 0;
            _healOnly[i] = false;
            _labels[i] = string.Empty;
            _iconNames[i] = string.Empty;
        }

        IReadOnlyList<QuickbarSlotSnapshot> source = _bound ?? ContentCatalog.Shipped.DefaultQuickbar;
        for (int n = 0; n < source.Count && n < SlotCount * 4; n++)
        {
            QuickbarSlotSnapshot entry = source[n];
            if ((uint)entry.Slot >= SlotCount || entry.Key is null || entry.Key.Length > MaxKeyChars)
            {
                continue;
            }

            ResolveEntry(entry);
        }

        IReadOnlyList<InventorySlotSnapshot>? bag = _inventory?.Slots;
        for (int n = 0; bag is not null && n < bag.Count; n++)
        {
            if (!IsBagConsumable(bag[n], out InventoryItemSnapshot item))
            {
                continue;
            }

            for (int i = 0; i < SlotCount; i++)
            {
                if (_slots[i].Kind != QuickbarEntryKind.Item
                    || !string.Equals(_slots[i].Key, item.ItemKind, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _itemCounts[i] += Math.Max(1, item.StackCount);
                if (_itemIds[i] == 0 || bag[n].SlotIndex < _itemBagSlots[i])
                {
                    _itemBagSlots[i] = bag[n].SlotIndex;
                    _itemIds[i] = item.ItemInstanceId;
                }
            }
        }
    }

    private void ResolveEntry(QuickbarSlotSnapshot entry)
    {
        int i = entry.Slot;
        ContentCatalog content = ContentCatalog.Shipped;
        if (entry.Kind == QuickbarEntryKind.Skill && content.TryGetSkill(entry.Key, out SkillDefinition skill))
        {
            _slots[i] = new QuickbarSlotSnapshot(i, QuickbarEntryKind.Skill, skill.Key);
            _manaCosts[i] = skill.ManaCost;
            _cooldownTotals[i] = skill.CooldownTicks;
            _healOnly[i] = skill.Kind == SkillKind.SelfHeal;
            _labels[i] = skill.DisplayName;
            _iconNames[i] = skill.IconKey ?? string.Empty;
        }
        else if (entry.Kind == QuickbarEntryKind.Item
            && content.TryGetItem(entry.Key, out ItemDefinition item)
            && item.Use is { } use)
        {
            _slots[i] = new QuickbarSlotSnapshot(i, QuickbarEntryKind.Item, item.Key);
            _cooldownTotals[i] = use.CooldownTicks;
            _labels[i] = item.DisplayName;
            _iconNames[i] = "item." + item.Key;
        }
    }
}
