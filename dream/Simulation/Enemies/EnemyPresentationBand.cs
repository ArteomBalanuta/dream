namespace Dream.Simulation.Enemies;

// The enemy speeds the client's presentation is built around, held here so the server can warn when an
// enemy_kinds row leaves them without referencing client or rendering code. EnemyVisualCatalog's gait
// band and ClientScenePresenter.EnemyTeleportDistance are defined from these.
public static class EnemyPresentationBand
{
    // The speed at which a moving enemy switches from its walk clip to its gallop clip, and the deadband
    // either side of it that keeps a speed near the threshold from flickering between the two.
    public const float GaitThresholdSpeed = 2.0f;
    public const float GaitHysteresisSpeed = 0.4f;

    // A tick-to-tick jump beyond this snaps a client-side enemy instead of interpolating it
    // (see ClientScenePresenter.EnemyTeleportDistance for why the number sits where it does).
    public const float TeleportDistance = 6.5f;
}
