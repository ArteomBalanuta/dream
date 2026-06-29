using OpenTK.Mathematics;

public sealed class RemotePlayerRenderer
{
    private static readonly Vector3 RemotePlayerColor = new(0.2f, 0.85f, 0.35f);
    private const float RemotePlayerCubeSide = 0.9f;

    private readonly Render _render;

    public RemotePlayerRenderer(Render render)
    {
        _render = render;
    }

    public void Draw(Camera camera, IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        foreach (RemotePlayerPresentationState remotePlayer in SelectVisiblePlayers(camera, remotePlayers))
        {
            _render.DrawSolidCube(camera, CreateModelMatrix(remotePlayer), RemotePlayerColor);
        }
    }

    public static IReadOnlyList<RemotePlayerPresentationState> SelectVisiblePlayers(
        Camera camera,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        List<RemotePlayerPresentationState> visible = [];
        foreach (RemotePlayerPresentationState remotePlayer in remotePlayers)
        {
            if (!remotePlayer.IsAlive)
            {
                continue;
            }

            Cube visibilityProbe = new()
            {
                Position = remotePlayer.Pose.Position
            };

            if (!WorldVisibility.IsCubeVisible(camera.Position, camera.Front, visibilityProbe))
            {
                continue;
            }

            visible.Add(remotePlayer);
        }

        return visible;
    }

    public static Matrix4 CreateModelMatrix(RemotePlayerPresentationState remotePlayer)
    {
        return Matrix4.CreateScale(RemotePlayerCubeSide) *
            Matrix4.CreateTranslation(remotePlayer.Pose.Position);
    }
}
