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

    // Windows
    public const string WinCrownInventory = "win.crown.inventory";
    public const string WinSlabInventory = "win.slab.inventory";
    public const string WinCrownCombine = "win.crown.combine";
    public const string WinSlabCombine = "win.slab.combine";
    public const string WinSideLeft = "win.side.left";
    public const string WinSideRight = "win.side.right";
    public const string WinBottom = "win.bottom";
    public const string WinStrip = "win.strip";
    public const string CellEmpty = "cell.empty";
    public const string CellOccupied = "cell.occupied";
    public const string CellHover = "cell.hover";
    public const string CellInvalid = "cell.invalid";
    public const string CellOverlayHover = "cell.overlay.hover";
    public const string CellOverlayInvalid = "cell.overlay.invalid";
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
    public const string GlyphCloseNormal = "glyph.close.normal";
    public const string GlyphCloseHover = "glyph.close.hover";

    // Cursors
    public const string CursorPointer = "cursor.pointer";
    public const string CursorPressed = "cursor.pressed";
    public const string CursorGrab = "cursor.grab";
    public const string CursorForbid = "cursor.forbid";
    public const string CursorAttack = "cursor.attack";

    private static readonly string[] DigitNames = ["dmg.0", "dmg.1", "dmg.2", "dmg.3", "dmg.4", "dmg.5", "dmg.6", "dmg.7", "dmg.8", "dmg.9"];

    // Damage digit sprite "dmg.<d>" for d in 0..9.
    public static string Digit(int d) => DigitNames[d];

    // Item icon sprite for an ItemKind such as "tool.torch": "item.tool.torch".
    public static string Item(string itemKind) => "item." + itemKind;

    public static IReadOnlyList<string> Required { get; } =
    [
        BaseLeft, BaseCentre, BaseRight, GapFill, DragonLeft, DragonRight, TalonLeft, TalonRight, CoordsPlate,
        GemHpEmpty, GemHpFull, GemMpEmpty, GaugeEmpty, GaugeCapLeft, GaugeCapRight,
        ExpCup, ExpFrame, ExpTrough, ExpFill, SkillTile, IconSatchel, IconButtonNormal, IconButtonHover, IconButtonPressed,
        WinCrownInventory, WinSlabInventory, WinCrownCombine, WinSlabCombine, WinSideLeft, WinSideRight, WinBottom, WinStrip,
        CellEmpty, CellOccupied, CellHover, CellInvalid, CellOverlayHover, CellOverlayInvalid,
        Well2x2, Well2x3, SilWeaponLeft, SilWeaponRight, SilHelm, SilArmor, TipPanel, TipBand,
        ButtonNormal, ButtonHover, ButtonPressed, GlyphCloseNormal, GlyphCloseHover,
        CursorPointer, CursorPressed, CursorGrab, CursorForbid, CursorAttack,
        .. DigitNames
    ];
}
