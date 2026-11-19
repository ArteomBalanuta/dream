using OpenTK.Mathematics;

namespace Dream.Rendering.Actors;

// The rarity tier of a held or worn item, in the order the loot tables read.
public enum EquipmentTier
{
    Common,
    Uncommon,
    Rare,
    Epic,
    Legendary
}

// Which EquipmentTint an item's visual id wears.
//
// An item's colour is its MATERIAL's colour (WeaponMeshCatalog.BaseColor / ArmourMeshCatalog.BaseColor)
// times a tint: a mild primary over the whole piece and a saturated accent that only the trim
// materials take (EquipmentSurface.AccentMix). Two swords made of the same steel can therefore
// differ at a glance - a cold slate-green glint on the fittings, a violet one - without either
// stopping to look like steel, which is what a wash over the whole piece would do.
//
// The palette is deliberately grim: the primary stays inside about 0.80..1.05 per channel, so no
// piece changes material; only the accent is saturated, and only the trim takes it. Nothing sits on
// EquipmentTint.MaxAlbedoChannel, so a tier reads as a hue and not as a flat clipped patch (the
// world pass has no tonemap - see the EquipmentSurface header).
//
// TO ADD A DYE OR RE-TIER AN ITEM: one entry. Re-tier = change the item's value in TierById. A new
// dye family = a new EquipmentTier value plus one line in TintFor. Nothing else reads these.
//
// A FUTURE PER-ITEM DYE (wire level) plugs in at Resolve: the server would send a dye id beside the
// visual id in PlayerEquipmentVisuals, and Resolve would take it and look the dye up in a second
// table before falling back to this tier table. Everything downstream - the renderers, the shader
// uniform, the albedo clamp in EquipmentTint.Apply - is already per-item and would not change.
public static class EquipmentTintCatalog
{
    // visual id -> tier. Judged by what the item IS: leather, tools and the plain stick are Common;
    // iron, chain and the one-handed war gear Uncommon; plate, steel and the two-handed Rare; the
    // grave, gossamer and arcane pieces Epic; the things that actually glow Legendary.
    private static readonly Dictionary<string, EquipmentTier> TierById = new(StringComparer.Ordinal)
    {
        // Held - common: the stick, the hand tools, the starting blade.
        ["held.generic"] = EquipmentTier.Common,
        ["held.tool.blade"] = EquipmentTier.Common,
        ["held.tool.builder"] = EquipmentTier.Common,
        ["held.tool.maul"] = EquipmentTier.Common,
        ["held.weapon.dagger"] = EquipmentTier.Common,
        ["held.weapon.hand-axe"] = EquipmentTier.Common,
        ["held.weapon.short-bow"] = EquipmentTier.Common,
        ["held.shield.buckler"] = EquipmentTier.Common,

        // Held - uncommon: the iron-and-wood one-handed war gear.
        ["held.weapon.sword"] = EquipmentTier.Uncommon,
        ["held.weapon.short-sword"] = EquipmentTier.Uncommon,
        ["held.weapon.iron-blade"] = EquipmentTier.Uncommon,
        ["held.weapon.axe"] = EquipmentTier.Uncommon,
        ["held.weapon.mace"] = EquipmentTier.Uncommon,
        ["held.weapon.spear"] = EquipmentTier.Uncommon,
        ["held.weapon.bow"] = EquipmentTier.Uncommon,
        ["held.shield.kite"] = EquipmentTier.Uncommon,

        // Held - rare: the great and the steel.
        ["held.weapon.greatsword"] = EquipmentTier.Rare,
        ["held.weapon.hammer"] = EquipmentTier.Rare,
        ["held.shield.tower"] = EquipmentTier.Rare,

        // Held - epic: the arcane focus, the bone staff and the warding lantern.
        ["held.weapon.staff"] = EquipmentTier.Epic,
        ["held.weapon.bone-staff"] = EquipmentTier.Epic,
        ["held.gear.ward-lantern"] = EquipmentTier.Epic,

        // Held - legendary: the two that carry a live flame.
        ["held.tool.torch"] = EquipmentTier.Legendary,
        ["held.tool.lantern"] = EquipmentTier.Legendary,

        // Worn - common: leather and the road cloak.
        ["worn.head.leather-cap"] = EquipmentTier.Common,
        ["worn.body.leather-vest"] = EquipmentTier.Common,
        ["worn.hands.leather-gloves"] = EquipmentTier.Common,
        ["worn.feet.leather-boots"] = EquipmentTier.Common,
        ["worn.legs.leather-leggings"] = EquipmentTier.Common,
        ["worn.back.travel-cloak"] = EquipmentTier.Common,

        // Worn - uncommon: iron and chain.
        ["worn.head.iron-helm"] = EquipmentTier.Uncommon,
        ["worn.body.chain-hauberk"] = EquipmentTier.Uncommon,
        ["worn.hands.iron-gauntlets"] = EquipmentTier.Uncommon,
        ["worn.feet.iron-boots"] = EquipmentTier.Uncommon,
        ["worn.legs.chain-chausses"] = EquipmentTier.Uncommon,
        ["worn.legs.plated-leggings"] = EquipmentTier.Uncommon,
        ["worn.back.hooded-cloak"] = EquipmentTier.Uncommon,

        // Worn - rare: full plate.
        ["worn.head.greathelm"] = EquipmentTier.Rare,
        ["worn.body.plate-cuirass"] = EquipmentTier.Rare,
        ["worn.hands.plate-gauntlets"] = EquipmentTier.Rare,
        ["worn.feet.plate-sabatons"] = EquipmentTier.Rare,
        ["worn.legs.plate-greaves"] = EquipmentTier.Rare,

        // Worn - epic: the grave mantle, the gossamer wings and the royal sorcerer's regalia -
        // enamelled indigo plate and gold, worn by what casts rather than what swings.
        ["worn.back.grave-mantle"] = EquipmentTier.Epic,
        ["worn.back.gossamer-wings"] = EquipmentTier.Epic,
        ["worn.head.sorcerer-crown"] = EquipmentTier.Epic,
        ["worn.body.sorcerer-regalia"] = EquipmentTier.Epic,
        ["worn.hands.sorcerer-gauntlets"] = EquipmentTier.Epic,
        ["worn.legs.sorcerer-greaves"] = EquipmentTier.Epic,
        ["worn.feet.sorcerer-sabatons"] = EquipmentTier.Epic,
        ["worn.back.sorcerer-mantle"] = EquipmentTier.Epic,

        // Worn - legendary: the ember that burns beside you.
        ["worn.companion.ember-wisp"] = EquipmentTier.Legendary,
    };

    // The shipped table, for tests and tooling. Read-only: a re-tier is an edit above, not a write.
    public static IReadOnlyDictionary<string, EquipmentTier> Tiers => TierById;

    // The tier of an id, or Common for an id this build does not know (a newer item, a modded one,
    // or null). An unknown item must look plain, never wild.
    public static EquipmentTier TierOf(string? visualId) =>
        visualId != null && TierById.TryGetValue(visualId, out EquipmentTier tier) ? tier : EquipmentTier.Common;

    // The tint an item wears. Deterministic, allocation free, and Neutral for anything unknown or
    // null - an unknown id draws exactly as it drew before tints existed.
    public static EquipmentTint Resolve(string? visualId) => TintFor(TierOf(visualId));

    // The dye family of a tier. Primary inside 0.80..1.05 per channel (no piece changes material);
    // accent saturated but never below 0.55 of a channel, so a tier can never destroy a material.
    public static EquipmentTint TintFor(EquipmentTier tier) => tier switch
    {
        // Plain. Identical to no tint at all.
        EquipmentTier.Common => EquipmentTint.Neutral,

        // Cold slate green: verdigris on the fittings of old iron.
        EquipmentTier.Uncommon => new EquipmentTint(new Vector3(0.96f, 1.00f, 0.96f), new Vector3(0.72f, 0.98f, 0.80f)),

        // Deep steel blue: a cold blued trim on war plate.
        EquipmentTier.Rare => new EquipmentTint(new Vector3(0.94f, 0.97f, 1.04f), new Vector3(0.66f, 0.82f, 1.05f)),

        // Violet: the grave and arcane work.
        EquipmentTier.Epic => new EquipmentTint(new Vector3(0.98f, 0.92f, 1.04f), new Vector3(0.92f, 0.66f, 1.08f)),

        // Dull gold and ember: warm, tarnished, never bright yellow.
        EquipmentTier.Legendary => new EquipmentTint(new Vector3(1.02f, 0.96f, 0.84f), new Vector3(1.05f, 0.86f, 0.58f)),

        _ => EquipmentTint.Neutral,
    };
}
