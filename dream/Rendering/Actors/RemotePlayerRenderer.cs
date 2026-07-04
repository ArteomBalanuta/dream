using OpenTK.Mathematics;
using Dream.Client.Presentation;
using Dream.Multiplayer.Diagnostics;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Rendering.Actors;


public sealed class RemotePlayerRenderer
{
    private readonly Render _render;
    private readonly Dictionary<int, AnimatedModel> _playerModels = [];
    private readonly PlayerAnimationClipTracker _clipTracker = new();

    public RemotePlayerRenderer(Render render)
    {
        _render = render;
    }

    public RemotePlayerRenderStats Draw(Camera camera, IReadOnlyList<RemotePlayerPresentationState> remotePlayers, float deltaTime)
    {
        PruneModelEntries(remotePlayers);

        IReadOnlyList<RemotePlayerPresentationState> visiblePlayers = SelectVisiblePlayers(camera, remotePlayers);
        foreach (RemotePlayerPresentationState remotePlayer in visiblePlayers)
        {
            PlayerRenderState renderState = PlayerRenderState.CreateRemote(remotePlayer);
            AnimatedModel model = GetOrCreateModel(remotePlayer.ActorId);
            _clipTracker.TryApplyClip(remotePlayer.ActorId, renderState.ClipName, model.PlayLoop);
            model.Update(deltaTime);

            Shader shader = _render.GetShader(renderState.ShaderName);
            model.BeginRender(shader, camera.GetViewMatrix(), _render.ProjectionMatrix, enableCulling: false);
            model.RenderPrepared(shader, renderState.ModelMatrix, renderState.NodeOverrides);
            model.EndRender();
        }

        return new RemotePlayerRenderStats(
            remotePlayers.Count,
            remotePlayers.Count(player => player.IsAlive),
            visiblePlayers.Count,
            _playerModels.Count);
    }

    public static IReadOnlyList<RemotePlayerPresentationState> SelectVisiblePlayers(
        Camera camera,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        List<RemotePlayerPresentationState> visible = [];
        foreach (RemotePlayerPresentationState remotePlayer in remotePlayers)
        {
            if ((remotePlayer.Pose.Position - camera.Position).LengthSquared >
                WorldVisibility.MaxRenderDistance * WorldVisibility.MaxRenderDistance)
            {
                continue;
            }

            visible.Add(remotePlayer);
        }

        return visible;
    }

    private AnimatedModel GetOrCreateModel(int actorId)
    {
        if (_playerModels.TryGetValue(actorId, out AnimatedModel? model))
        {
            return model;
        }

        AnimatedModel created = ModelAssets.CreatePlayerInstance();
        _playerModels[actorId] = created;
        NetworkDiagnosticsLog.Write("client.render.remote.model", $"created actor={actorId} cached={_playerModels.Count}");
        return created;
    }

    private void PruneModelEntries(IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        HashSet<int> activeActorIds = remotePlayers
            .Select(player => player.ActorId)
            .ToHashSet();
        _clipTracker.RemoveExcept(activeActorIds);

        foreach (int actorId in _playerModels.Keys.ToArray())
        {
            if (activeActorIds.Contains(actorId))
            {
                continue;
            }

            _playerModels[actorId].Dispose();
            _playerModels.Remove(actorId);
            NetworkDiagnosticsLog.Write("client.render.remote.model", $"pruned actor={actorId} cached={_playerModels.Count}");
        }
    }
}
