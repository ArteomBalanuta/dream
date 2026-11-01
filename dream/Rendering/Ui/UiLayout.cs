namespace Dream.Rendering.Ui;

// Screen rects of the classic HUD for one viewport and scale. Pure math, no state.
// Units: 1 u = Scale pixels. The bar is 640 x 48 u in three groups (split at 199 and 441 u in
// hud.json) anchored bottom-left, bottom-centre and bottom-right; the gaps between them are
// filled with the tiled gapfill sprite. Windows are 190 x 432 u: inventory at the top right,
// character window directly to its left. The bar draws above the windows.
public readonly record struct UiLayout(
    int ViewportWidth,
    int ViewportHeight,
    float Scale,
    UiRect Bar,
    UiRect LeftGroup,
    UiRect CentreGroup,
    UiRect RightGroup,
    UiRect LeftGap,
    UiRect RightGap,
    UiRect Inventory,
    UiRect Character)
{
    public const float BarWidthUnits = 640f;
    public const float BarHeightUnits = 48f;
    public const float LeftGroupUnits = 199f;
    public const float RightGroupStartUnits = 441f;
    public const float WindowWidthUnits = 190f;
    public const float WindowHeightUnits = 432f;

    // The art is authored at 4 texels per unit.
    public const float ArtTexelsPerUnit = 4f;

    // Screen pixels per sprite texel: what 9-slice borders and tile steps scale by.
    public float PixelsPerTexel => Scale / ArtTexelsPerUnit;

    // Multiplier on the baked font sizes. Panels lay text out in baked pixels (13 and 26), so it stays
    // 1 until a scale where whole-number doubling is both needed and crisp.
    public float TextScale => MathF.Max(1f, MathF.Floor(Scale / 2f));

    // configuredScale 0 is auto (see UiScale.Resolve).
    public static UiLayout Create(int width, int height, float configuredScale)
    {
        float s = UiScale.Resolve(width, height, configuredScale);
        float w = width;
        float h = height;

        float barH = BarHeightUnits * s;
        float barY = h - barH;
        float leftW = LeftGroupUnits * s;
        float centreW = (RightGroupStartUnits - LeftGroupUnits) * s;
        float rightW = (BarWidthUnits - RightGroupStartUnits) * s;

        UiRect left = new(0f, barY, leftW, barH);
        float centreX = MathF.Round((w - centreW) * 0.5f);
        UiRect centre = new(centreX, barY, centreW, barH);
        UiRect right = new(w - rightW, barY, rightW, barH);
        UiRect leftGap = new(left.Right, barY, MathF.Max(0f, centre.X - left.Right), barH);
        UiRect rightGap = new(centre.Right, barY, MathF.Max(0f, right.X - centre.Right), barH);

        float winW = WindowWidthUnits * s;
        float winH = WindowHeightUnits * s;
        UiRect inventory = new(w - winW, 0f, winW, winH);
        UiRect character = new(inventory.X - winW, 0f, winW, winH);

        return new UiLayout(width, height, s, new UiRect(0f, barY, w, barH), left, centre, right, leftGap, rightGap, inventory, character);
    }

    // A rect given in units relative to origin (a window or bar group), scaled to pixels.
    public UiRect FromUnits(UiRect origin, float x, float y, float w, float h) =>
        new(origin.X + x * Scale, origin.Y + y * Scale, w * Scale, h * Scale);
}
