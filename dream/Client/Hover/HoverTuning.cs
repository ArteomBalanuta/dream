namespace Dream.Client.Hover;

// Every number the hover pipeline is tuned by. The lifecycle table in the design (4.4) is pinned to these by tests.
public static class HoverTuning
{
    // The pointer must move farther than this (pixels, from where the last pick was made) to force a new pick.
    public const float RepickPixels = 2f;

    // A still pointer still re-picks this often, because things move under it.
    public const float RefreshSeconds = 0.10f;

    // The current target's volume is scaled by this when tested, so the hover does not flicker at its edge.
    public const float StickyInflate = 1.25f;

    // How long the pointer must rest on a target before the info panel starts to appear.
    public const float PanelDwellSeconds = 0.10f;

    public const float PanelFadeInSeconds = 0.08f;

    // After the pointer leaves, the panel keeps the last target this long, then fades.
    public const float LingerSeconds = 0.15f;

    public const float FadeOutSeconds = 0.10f;

    // A target that dies or vanishes while shown stays on the panel (defeated) this long, then fades.
    public const float DefeatedHoldSeconds = 0.25f;
}
