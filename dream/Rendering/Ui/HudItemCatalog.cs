using Dream.Simulation.Inventory;

namespace Dream.Rendering.Ui;

// Item kind -> icon sprite ("item.<ItemKind>") and rarity -> tier. The lookup ignores case. An item
// the art does not cover (or a hostile or empty kind) has no icon and the window draws its name.
public sealed class HudItemCatalog
{
    private const string Prefix = "item.";

    private readonly Dictionary<string, UiSprite> _icons = new(StringComparer.OrdinalIgnoreCase);

    public HudItemCatalog(UiSpriteTable sprites)
    {
        ArgumentNullException.ThrowIfNull(sprites);
        foreach (string name in sprites.Names)
        {
            if (name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                && sprites.TryGet(name, out UiSprite sprite))
            {
                _icons[name] = sprite;
            }
        }
    }

    public bool TryGetIcon(string? itemKind, out UiSprite icon)
    {
        icon = default;
        if (string.IsNullOrEmpty(itemKind))
        {
            return false;
        }

        // The key is built per call only on a miss path elsewhere; here a short concat is the cost
        // of a lookup, and HudViewBuilder calls it once per item per snapshot change, not per frame.
        return _icons.TryGetValue(UiSpriteNames.Item(itemKind), out icon);
    }

    // An undefined rarity value draws as Common.
    public static ItemTier TierOf(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Uncommon => ItemTier.Uncommon,
        ItemRarity.Rare => ItemTier.Rare,
        _ => ItemTier.Common
    };

    public HudItemView Describe(in InventoryItemSnapshot item, bool equipped)
    {
        bool hasIcon = TryGetIcon(item.ItemKind, out UiSprite icon);
        return new HudItemView(item, hasIcon, hasIcon ? icon : default, TierOf(item.Rarity), equipped);
    }
}
