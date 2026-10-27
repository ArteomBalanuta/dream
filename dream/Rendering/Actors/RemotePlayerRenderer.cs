using System.Runtime.ExceptionServices;
using OpenTK.Mathematics;
using Dream.Client.Presentation;
using Dream.Multiplayer.Diagnostics;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Rendering.Actors;


public sealed class RemotePlayerRenderer : IDisposable
{
    private readonly Render _render;
    private readonly Dictionary<int, AnimatedModel> _playerModels = [];
    private readonly PlayerAnimationClipTracker _clipTracker = new();
    private readonly HashSet<int> _activeActorIds = [];
    private readonly List<int> _staleActorIds = [];
    private readonly List<RemotePlayerPresentationState> _visiblePlayers = [];

    public RemotePlayerRenderer(Render render)
    {
        _render = render;
    }

    public RemotePlayerRenderStats Draw(Camera camera, IReadOnlyList<RemotePlayerPresentationState> remotePlayers, float deltaTime)
    {
        PruneModelEntries(remotePlayers);

        SelectVisiblePlayers(camera, remotePlayers, _visiblePlayers);
        for (int i = 0; i < _visiblePlayers.Count; i++)
        {
            RemotePlayerPresentationState remotePlayer = _visiblePlayers[i];
            PlayerRenderState renderState = PlayerRenderState.CreateRemote(remotePlayer);
            AnimatedModel model = GetOrCreateModel(remotePlayer.ActorId);
            _clipTracker.TryApplyClip(remotePlayer.ActorId, renderState.ClipName, model);
            model.Update(deltaTime);

            Shader shader = _render.GetShader(renderState.ShaderName);
            model.BeginRender(shader, camera.GetViewMatrix(), _render.ProjectionMatrix, enableCulling: false);
            model.RenderPrepared(shader, renderState.ModelMatrix, renderState.NodeOverrides);
            model.EndRender();
        }

        return new RemotePlayerRenderStats(
            remotePlayers.Count,
            CountAlive(remotePlayers),
            _visiblePlayers.Count,
            _playerModels.Count);
    }

    public static IReadOnlyList<RemotePlayerPresentationState> SelectVisiblePlayers(
        Camera camera,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        List<RemotePlayerPresentationState> visible = [];
        SelectVisiblePlayers(camera, remotePlayers, visible);
        return visible;
    }

    // Clears and refills the caller's list, so a renderer can reuse one list every frame.
    public static void SelectVisiblePlayers(
        Camera camera,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers,
        List<RemotePlayerPresentationState> visible)
    {
        visible.Clear();
        for (int i = 0; i < remotePlayers.Count; i++)
        {
            RemotePlayerPresentationState remotePlayer = remotePlayers[i];
            if ((remotePlayer.Pose.Position - camera.Position).LengthSquared >
                WorldVisibility.MaxRenderDistance * WorldVisibility.MaxRenderDistance)
            {
                continue;
            }

            visible.Add(remotePlayer);
        }
    }

    public static int CountAlive(IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        int alive = 0;
        for (int i = 0; i < remotePlayers.Count; i++)
        {
            if (remotePlayers[i].IsAlive)
            {
                alive++;
            }
        }

        return alive;
    }

    public static void CollectActiveActorIds(
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers,
        HashSet<int> activeActorIds)
    {
        activeActorIds.Clear();
        for (int i = 0; i < remotePlayers.Count; i++)
        {
            activeActorIds.Add(remotePlayers[i].ActorId);
        }
    }

    // Fills staleActorIds with the cached actors that are no longer active. The caller removes
    // them afterwards, so the dictionary is never modified while it is enumerated.
    public static void CollectStaleActorIds<TModel>(
        Dictionary<int, TModel> cachedModels,
        IReadOnlySet<int> activeActorIds,
        List<int> staleActorIds)
    {
        staleActorIds.Clear();
        foreach (int actorId in cachedModels.Keys)
        {
            if (!activeActorIds.Contains(actorId))
            {
                staleActorIds.Add(actorId);
            }
        }
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

    // Disposes the per-instance player models that are still cached; pruning already disposed
    // the others. These models come from ModelAssets.CreatePlayerInstance, so this renderer owns
    // them. A second call finds the cache empty.
    public void Dispose()
    {
        _activeActorIds.Clear();

        List<Exception>? failures = null;
        foreach (AnimatedModel model in _playerModels.Values)
        {
            try
            {
                model.Dispose();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        _playerModels.Clear();
        try
        {
            _clipTracker.RemoveExcept(_activeActorIds);
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        if (failures == null)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(failures);
    }

    private void PruneModelEntries(IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        CollectActiveActorIds(remotePlayers, _activeActorIds);
        _clipTracker.RemoveExcept(_activeActorIds);

        CollectStaleActorIds(_playerModels, _activeActorIds, _staleActorIds);
        for (int i = 0; i < _staleActorIds.Count; i++)
        {
            int actorId = _staleActorIds[i];
            _playerModels[actorId].Dispose();
            _playerModels.Remove(actorId);
            NetworkDiagnosticsLog.Write("client.render.remote.model", $"pruned actor={actorId} cached={_playerModels.Count}");
        }
    }
}
