namespace Dream.Rendering.Ui.Menu;

// The sprites the front end draws with. The menu.* art is preferred; when a sprite is missing the
// classic window plates stand in (buttons, panel) or the element is drawn flat, so a damaged art page
// never leaves the menu unusable.
public sealed class MenuSkin
{
    public const string Backdrop = "menu.backdrop";
    public const string Title = "menu.title";
    public const string ButtonNormal = "menu.button.normal";
    public const string ButtonHover = "menu.button.hover";
    public const string ButtonPressed = "menu.button.pressed";
    public const string ButtonDisabled = "menu.button.disabled";
    public const string Panel = "menu.panel";
    public const string SliderTrack = "menu.slider.track";
    public const string SliderKnob = "menu.slider.knob";
    public const string Divider = "menu.divider";

    public UiSprite? BackdropSprite { get; private init; }

    public UiSprite? TitleSprite { get; private init; }

    public UiSprite? ButtonNormalSprite { get; private init; }

    public UiSprite? ButtonHoverSprite { get; private init; }

    public UiSprite? ButtonPressedSprite { get; private init; }

    public UiSprite? ButtonDisabledSprite { get; private init; }

    public UiSprite? PanelSprite { get; private init; }

    public UiSprite? SliderTrackSprite { get; private init; }

    public UiSprite? SliderKnobSprite { get; private init; }

    public UiSprite? DividerSprite { get; private init; }

    // True when the dedicated menu art is in the table.
    public bool HasMenuArt { get; private init; }

    public static MenuSkin From(UiSpriteTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new MenuSkin
        {
            BackdropSprite = Find(table, Backdrop),
            TitleSprite = Find(table, Title),
            ButtonNormalSprite = Find(table, ButtonNormal) ?? Find(table, UiSpriteNames.ButtonNormal),
            ButtonHoverSprite = Find(table, ButtonHover) ?? Find(table, UiSpriteNames.ButtonHover),
            ButtonPressedSprite = Find(table, ButtonPressed) ?? Find(table, UiSpriteNames.ButtonPressed),
            ButtonDisabledSprite = Find(table, ButtonDisabled) ?? Find(table, UiSpriteNames.ButtonDisabled),
            PanelSprite = Find(table, Panel) ?? Find(table, UiSpriteNames.TipPanel),
            SliderTrackSprite = Find(table, SliderTrack),
            SliderKnobSprite = Find(table, SliderKnob),
            DividerSprite = Find(table, Divider),
            HasMenuArt = table.Contains(ButtonNormal) && table.Contains(Panel)
        };
    }

    private static UiSprite? Find(UiSpriteTable table, string name) =>
        table.TryGet(name, out UiSprite sprite) ? sprite : null;
}
