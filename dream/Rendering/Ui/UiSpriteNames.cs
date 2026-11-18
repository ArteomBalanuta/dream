namespace Dream.Rendering.Ui;

// Sprite names the classic HUD cannot draw without. UiSpriteTable.Validate fails (and the surface
// falls back to the ImGui HUD) when any is missing. Item icons are not required: an item with no
// sprite just shows no icon.
public static class UiSpriteNames
{
    // Status bar
    public const string BaseLeft = "hud.base.left";
    public const string BaseCentre = "hud.base.centre";
    public const string BaseRight = "hud.base.right";
    public const string GapFill = "hud.gapfill";
    public const string DragonLeft = "hud.dragon.left";
    public const string DragonRight = "hud.dragon.right";
    public const string TalonLeft = "hud.talon.left";
    public const string TalonRight = "hud.talon.right";
    public const string CoordsPlate = "hud.coordsplate";
    public const string GemHpEmpty = "gem.hp.empty";
    public const string GemHpFull = "gem.hp.full";
    public const string GemMpEmpty = "gem.mp.empty";
    public const string GaugeEmpty = "gauge.empty";
    public const string GaugeCapLeft = "gauge.cap.left";
    public const string GaugeCapRight = "gauge.cap.right";
    public const string ExpCup = "exp.cup";
    public const string ExpFrame = "exp.frame";
    public const string ExpTrough = "exp.trough";
    public const string ExpFill = "exp.fill";
    public const string SkillTile = "skill.tile.normal";
    public const string IconSatchel = "icon.satchel";
    public const string IconButtonNormal = "iconbtn.frame.normal";
    public const string IconButtonHover = "iconbtn.frame.hover";
    public const string IconButtonPressed = "iconbtn.frame.pressed";
    public const string IconButtonPlinth = "iconbtn.plinth";
    public const string SlabNormal = "hud.slab.normal";
    public const string ExpTick = "exp.tick";
    public const string SkillBracket = "skill.bracket";
    public const string SkillTileDisabled = "skill.tile.disabled";
    public const string SkillIconDefault = "skill.icon.default";
    public const string QweSlotQ = "qwe.slot.q.normal";
    public const string QweSlotW = "qwe.slot.w.normal";
    public const string QweSlotE = "qwe.slot.e.normal";
    public const string QweNotch = "qwe.notch";

    // Windows
    public const string WinCrownInventory = "win.crown.inventory";
    public const string WinSlabInventory = "win.slab.inventory";
    public const string WinCrownCombine = "win.crown.combine";
    public const string WinSlabCombine = "win.slab.combine";
    public const string WinSideLeft = "win.side.left";
    public const string WinSideRight = "win.side.right";
    public const string WinBottom = "win.bottom";
    public const string WinStrip = "win.strip";
    public const string WinScrollRod = "win.scroll.rod";
    public const string WinLineBox = "win.linebox";
    public const string CellEmpty = "cell.empty";
    public const string CellOccupied = "cell.occupied";
    public const string CellHover = "cell.hover";
    public const string CellInvalid = "cell.invalid";
    public const string CellOverlayHover = "cell.overlay.hover";
    public const string CellOverlayInvalid = "cell.overlay.invalid";
    // The grid shop's sprites (window page). These ARE in Required, because the shipped window atlas
    // carries them. They were held out of Required only while the panels were written and the art was
    // not yet merged; the panels still draw each through TryGet, which is belt and braces, not a
    // licence to remove one. Dropping or renaming any of these without also taking it out of Required
    // makes UiSpriteTable.Validate throw and silently drops the WHOLE classic HUD back to the ImGui
    // HUD at runtime.
    public const string ShopCellEmpty = "shop.cell.empty";
    public const string ShopCellOccupied = "shop.cell.occupied";
    public const string ShopFrameEmber = "shop.frame.ember";
    public const string ShopFrameCold = "shop.frame.cold";
    public const string CellOverlayEmber = "cell.overlay.ember";
    public const string Well2x2 = "well.2x2";
    public const string Well2x3 = "well.2x3";
    public const string SilWeaponLeft = "sil.weapon.left";
    public const string SilWeaponRight = "sil.weapon.right";
    public const string SilHelm = "sil.helm";
    public const string SilArmor = "sil.armor";
    public const string TipPanel = "tip.panel";
    public const string TipBand = "tip.band";
    public const string ButtonNormal = "btn.plate24.normal";
    public const string ButtonHover = "btn.plate24.hover";
    public const string ButtonPressed = "btn.plate24.pressed";
    public const string ButtonDisabled = "btn.plate24.disabled";
    public const string Plate44Normal = "btn.plate44x32.normal";
    public const string Plate44Hover = "btn.plate44x32.hover";
    public const string Plate44Pressed = "btn.plate44x32.pressed";
    public const string GlyphCloseNormal = "glyph.close.normal";
    public const string GlyphCloseHover = "glyph.close.hover";

    // Classic HUD iteration 2. The bezels, spill light and numerals are in Required (PendingRequired);
    // the panels draw the rest when the atlas has them and go without when it does not.
    public const string GemMpFull = "gem.mp.full";
    public const string GemHpMask = "gem.hp.mask";
    public const string GemHpSpec = "gem.hp.spec";
    public const string GemMpMask = "gem.mp.mask";
    public const string GemMpSpec = "gem.mp.spec";
    public const string GemSettingLeft = "gem.setting.left";
    public const string GemSettingRight = "gem.setting.right";
    public const string GemHpSpill = "gem.hp.spill";
    public const string GemMpSpill = "gem.mp.spill";
    public const string HudNumSlash = "hudnum.slash";
    public const string Well1x1 = "well.1x1";
    public const string Well3x2 = "well.3x2";
    public const string SilGloves = "sil.gloves";
    public const string SilBoots = "sil.boots";
    public const string SilPants = "sil.pants";
    public const string SilPendant = "sil.pendant";
    public const string SilRing = "sil.ring";
    public const string SilWings = "sil.wings";
    public const string SilPet = "sil.pet";

    // Cursors
    public const string CursorPointer = "cursor.pointer";
    public const string CursorPressed = "cursor.pressed";
    public const string CursorGrab = "cursor.grab";
    public const string CursorForbid = "cursor.forbid";
    public const string CursorAttack = "cursor.attack";

    private static readonly string[] DigitNames = ["dmg.0", "dmg.1", "dmg.2", "dmg.3", "dmg.4", "dmg.5", "dmg.6", "dmg.7", "dmg.8", "dmg.9"];

    // Damage digit sprite "dmg.<d>" for d in 0..9.
    public static string Digit(int d) => DigitNames[d];

    private static readonly string[] HudNumNames = ["hudnum.0", "hudnum.1", "hudnum.2", "hudnum.3", "hudnum.4", "hudnum.5", "hudnum.6", "hudnum.7", "hudnum.8", "hudnum.9"];

    // Bar numeral sprite "hudnum.<d>" for d in 0..9.
    public static string HudNum(int d) => HudNumNames[d];

    // Item icon sprite for an ItemKind such as "tool.torch": "item.tool.torch".
    public static string Item(string itemKind) => "item." + itemKind;

    // Iteration 2, WS3 (gem runtime and bar panel). The bar art that arrived with the forged atlas: the
    // bezels under the gems, the gems' spill light and the bar numerals. Part of Required. The gem masks,
    // glints and gem.mp.full are not: the panel falls back to the plain clipped gem without them.
    public static IReadOnlyList<string> PendingRequired { get; } =
    [
        GemSettingLeft, GemSettingRight, GemHpSpill, GemMpSpill, HudNumSlash, .. HudNumNames
    ];

    public static IReadOnlyList<string> Required { get; } =
    [
        BaseLeft, BaseCentre, BaseRight, GapFill, DragonLeft, DragonRight, TalonLeft, TalonRight, CoordsPlate,
        GemHpEmpty, GemHpFull, GemMpEmpty, GaugeEmpty, GaugeCapLeft, GaugeCapRight,
        ExpCup, ExpFrame, ExpTrough, ExpFill, SkillTile, IconSatchel, IconButtonNormal, IconButtonHover, IconButtonPressed,
        IconButtonPlinth, SlabNormal, ExpTick, SkillBracket, SkillTileDisabled, SkillIconDefault, QweSlotQ, QweSlotW, QweSlotE, QweNotch,
        WinCrownInventory, WinSlabInventory, WinCrownCombine, WinSlabCombine, WinSideLeft, WinSideRight, WinBottom, WinStrip,
        WinScrollRod, WinLineBox,
        CellEmpty, CellOccupied, CellHover, CellInvalid, CellOverlayHover, CellOverlayInvalid,
        Well2x2, Well2x3, SilWeaponLeft, SilWeaponRight, SilHelm, SilArmor, TipPanel, TipBand,
        ShopCellEmpty, ShopCellOccupied, ShopFrameEmber, ShopFrameCold, CellOverlayEmber,
        ButtonNormal, ButtonHover, ButtonPressed, Plate44Normal, Plate44Hover, Plate44Pressed, GlyphCloseNormal, GlyphCloseHover,
        CursorPointer, CursorPressed, CursorGrab, CursorForbid, CursorAttack,
        .. DigitNames,
        .. PendingRequired
    ];
}
