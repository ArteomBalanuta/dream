using Dream.Client.Presentation;

namespace Dream.Client.Scenes;

public partial class PlayScene
{
    private readonly ClientWorldClock _worldClock = new();

    // Replicated time of day at the tick remote players render at (monotonic, so the sky never steps back).
    private float WorldTimeOfDay01 => _worldClock.TimeOfDay01(RemotePlayerRenderTick);
}
