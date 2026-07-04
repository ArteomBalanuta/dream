namespace Dream.Rendering.Actors;

public readonly record struct RemotePlayerRenderStats(
    int InputCount,
    int AliveCount,
    int VisibleCount,
    int ModelCount);
