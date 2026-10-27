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
    private readonly Dictionary<int, AsyncResourceHandle<AnimatedModel>> _playerModels = [];
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
            // The actor's model is requested in PruneModelEntries and loads in the background.
            // Until it is uploaded the actor is skipped (its name label is drawn by the HUD), and
            // no clip is recorded, so the current clip is applied on the first drawn frame.
            if (!_playerModels.TryGetValue(remotePlayer.ActorId, out AsyncResourceHandle<AnimatedModel>? handle) ||
                !handle.TryGet(out AnimatedModel? model))
            {
                continue;
            }

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

    // Disposes and removes the cached entries of actors that are no longer active. Returns how
    // many were removed. For a model handle that is still decoding, disposing discards the
    // request, so a model for an actor that left is never uploaded.
    public static int PruneStaleEntries<THandle>(
        Dictionary<int, THandle> cachedModels,
        IReadOnlySet<int> activeActorIds,
        List<int> staleActorIds)
        where THandle : IDisposable
    {
        CollectStaleActorIds(cachedModels, activeActorIds, staleActorIds);
        for (int i = 0; i < staleActorIds.Count; i++)
        {
            int actorId = staleActorIds[i];
            THandle handle = cachedModels[actorId];
            cachedModels.Remove(actorId);
            handle.Dispose();
        }

        return staleActorIds.Count;
    }

    // Requests a per-instance model (ModelAssets.RequestPlayerInstance()) for every active actor
    // that has none, visible or not, so a player walking into view is usually already loaded.
    private void RequestMissingModels(IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        for (int i = 0; i < remotePlayers.Count; i++)
        {
            int actorId = remotePlayers[i].ActorId;
            if (_playerModels.ContainsKey(actorId))
            {
                continue;
            }

            _playerModels[actorId] = ModelAssets.RequestPlayerInstance();
            NetworkDiagnosticsLog.Write("client.render.remote.model", $"requested actor={actorId} cached={_playerModels.Count}");
        }
    }

    // Disposes the per-instance model handles that are still cached; pruning already disposed
    // the others. The handles come from ModelAssets.RequestPlayerInstance(), so this renderer owns
    // them; disposing a handle releases its model or discards its pending decode. A second call
    // finds the cache empty, and a handle the model cache's teardown already released is a no-op.
    public void Dispose()
    {
        _activeActorIds.Clear();

        List<Exception>? failures = null;
        foreach (AsyncResourceHandle<AnimatedModel> model in _playerModels.Values)
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

        PruneStaleEntries(_playerModels, _activeActorIds, _staleActorIds);
        for (int i = 0; i < _staleActorIds.Count; i++)
        {
            NetworkDiagnosticsLog.Write("client.render.remote.model", $"pruned actor={_staleActorIds[i]} cached={_playerModels.Count}");
        }

        RequestMissingModels(remotePlayers);
    }
}
