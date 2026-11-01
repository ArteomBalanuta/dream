using System.Globalization;
using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;

namespace Dream.Rendering.Ui.Panels;

// Tier look of an item: which icon page, which name colour. Pure; the HUD view builder fills
// HudItemView.Tier from the rarity with TierOf, and the panels read the rest from here.
// Colours (spec 02 and the plan, decision 6): Common #F9F8FA, Uncommon rose #F08FB0, Rare mint
// #5FD6A0. The ImGui path keeps its own colours.
public static class ItemTierStyle
{
    public static readonly UiColor Common = new(0xF9, 0xF8, 0xFA, 255);
    public static readonly UiColor Uncommon = new(0xF0, 0x8F, 0xB0, 255);
    public static readonly UiColor Rare = new(0x5F, 0xD6, 0xA0, 255);

    // Panel text colours that are not tier colours.
    public static readonly UiColor Title = new(0xFD, 0xFD, 0xFE, 255);
    public static readonly UiColor Body = new(0xE9, 0xEA, 0xF3, 255);
    public static readonly UiColor Dim = new(0xA6, 0xA6, 0xAE, 255);
    public static readonly UiColor Bonus = new(0xA0, 0xB3, 0xD5, 255);
    public static readonly UiColor Danger = new(0xF4, 0x66, 0x50, 255);
    public static readonly UiColor Shadow = new(0, 0, 0, 170);

    // Same mapping as CharacterPanelModel.ColorKey, so a test can pin the two together. A rarity
    // the enum does not name (a hostile snapshot) is Common.
    public static ItemTier TierOf(ItemRarity rarity) => CharacterPanelModel.ColorKey(rarity) switch
    {
        RarityColorKey.Uncommon => ItemTier.Uncommon,
        RarityColorKey.Rare => ItemTier.Rare,
        _ => ItemTier.Common
    };

    public static UiColor ColorOf(ItemTier tier) => tier switch
    {
        ItemTier.Uncommon => Uncommon,
        ItemTier.Rare => Rare,
        _ => Common
    };

    // Each tier has its own icon page; the rects are the common page's.
    public static UiPage PageOf(ItemTier tier) => tier switch
    {
        ItemTier.Uncommon => UiPage.ItemsUncommon,
        ItemTier.Rare => UiPage.ItemsRare,
        _ => UiPage.ItemsCommon
    };

    public static UiSprite IconFor(in UiSprite icon, ItemTier tier) => icon with { Page = PageOf(tier) };

    public static UiColor WithAlpha(UiColor color, float opacity) =>
        color.WithAlpha((byte)Math.Clamp((int)MathF.Round(color.A * Math.Clamp(opacity, 0f, 1f)), 0, 255));
}

// Builds a short line in a caller-owned buffer so panels can draw "Strength 10 +2 = 12" without
// allocating. Overlong text is cut, never thrown.
internal ref struct UiTextBuilder
{
    private readonly Span<char> _buffer;
    private int _length;

    public UiTextBuilder(Span<char> buffer)
    {
        _buffer = buffer;
        _length = 0;
    }

    public readonly ReadOnlySpan<char> Text => _buffer[.._length];

    public void Append(ReadOnlySpan<char> text)
    {
        int n = Math.Min(text.Length, _buffer.Length - _length);
        text[..n].CopyTo(_buffer[_length..]);
        _length += n;
    }

    public void Append(int value)
    {
        if (value.TryFormat(_buffer[_length..], out int written, default, CultureInfo.InvariantCulture))
        {
            _length += written;
        }
    }
}
