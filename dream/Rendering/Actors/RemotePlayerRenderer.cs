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
    // Model streaming: a remote player's animation instance handle is requested once the player is
    // within ModelRequestDistance (render distance plus a margin, so it is usually ready before it
    // comes into view) and released once the player is beyond the larger ModelReleaseDistance. The
    // gap between the two is hysteresis, so a player hovering at the edge never re-requests every
    // frame. Every handle shares one decode and upload of the player model; while it is still
    // loading at most MaxPendingModelRequests handles wait on it, and further requests wait for a
    // free slot.
    public const float ModelRequestDistance = WorldVisibility.MaxRenderDistance * 1.25f;
    public const float ModelReleaseDistance = WorldVisibility.MaxRenderDistance * 1.5f;
    public const int MaxPendingModelRequests = 4;

    private readonly Render _render;
    private readonly Dictionary<int, AnimatedModelInstanceHandle> _playerModels = [];
    private readonly PlayerAnimationClipTracker _clipTracker = new();
    private readonly HashSet<int> _activeActorIds = [];
    // The session each active actor id belonged to on the last Draw. The server reuses a player
    // actor id once its session leaves, so the same id can carry another player from one frame to
    // the next; that player must not inherit the old player's instance or clip.
    private readonly Dictionary<int, string> _actorSessionIds = [];
    private readonly List<int> _reassignedActorIds = [];
    private readonly List<int> _staleActorIds = [];
    private readonly List<int> _modelActorIds = [];
    private readonly List<RemotePlayerPresentationState> _visiblePlayers = [];

    public RemotePlayerRenderer(Render render)
    {
        _render = render;
    }

    // Instance handles whose shared model is still decoding or waiting for upload, as of the last Draw.
    public int PendingModelCount { get; private set; }

    public RemotePlayerRenderStats Draw(Camera camera, IReadOnlyList<RemotePlayerPresentationState> remotePlayers, float deltaTime)
    {
        PruneModelEntries(remotePlayers);
        ReleaseReassignedActors(remotePlayers);
        ReleaseFarModels(camera.Position, remotePlayers);
        RequestMissingModels(camera.Position, remotePlayers);

        SelectVisiblePlayers(camera, remotePlayers, _visiblePlayers);
        for (int i = 0; i < _visiblePlayers.Count; i++)
        {
            RemotePlayerPresentationState remotePlayer = _visiblePlayers[i];
            PlayerRenderState renderState = PlayerRenderState.CreateRemote(remotePlayer);
            // The actor's instance is requested in RequestMissingModels; the shared player model
            // loads in the background. Until it is uploaded the actor is skipped (its name label is
            // drawn by the HUD), and no clip is recorded, so the current clip is applied on the
            // first drawn frame. Each player advances its own instance once per frame.
            if (!_playerModels.TryGetValue(remotePlayer.ActorId, out AnimatedModelInstanceHandle? handle) ||
                !handle.TryGet(out AnimatedModel? model, out AnimatedModelInstance? instance))
            {
                continue;
            }

            _clipTracker.TryApplyClip(remotePlayer.ActorId, renderState.ClipName, instance);
            instance.Update(deltaTime);

            Shader shader = _render.GetShader(renderState.ShaderName);
            model.BeginRender(shader, camera.GetViewMatrix(), _render.ProjectionMatrix, enableCulling: false);
            model.RenderPrepared(shader, instance, renderState.ModelMatrix, renderState.NodeOverrides);
            model.EndRender();
        }

        // ModelCount reports uploaded models only; requests still loading are PendingModelCount.
        CountModelStates(_playerModels, out int readyModels, out int pendingModels);
        PendingModelCount = pendingModels;
        return new RemotePlayerRenderStats(
            remotePlayers.Count,
            CountAlive(remotePlayers),
            _visiblePlayers.Count,
            readyModels);
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

    // Records each active actor's session in actorSessionIds and fills reassignedActorIds with the
    // actors whose id was recorded under another session: the id now belongs to a different player.
    // Entries of inactive actors are left alone; PruneStaleSessions drops them.
    public static void CollectReassignedActorIds(
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers,
        Dictionary<int, string> actorSessionIds,
        List<int> reassignedActorIds)
    {
        reassignedActorIds.Clear();
        for (int i = 0; i < remotePlayers.Count; i++)
        {
            RemotePlayerPresentationState remotePlayer = remotePlayers[i];
            if (actorSessionIds.TryGetValue(remotePlayer.ActorId, out string? sessionId) &&
                string.Equals(sessionId, remotePlayer.SessionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (sessionId != null)
            {
                reassignedActorIds.Add(remotePlayer.ActorId);
            }

            actorSessionIds[remotePlayer.ActorId] = remotePlayer.SessionId;
        }
    }

    // Removes the recorded sessions of actors that are no longer active.
    public static void PruneStaleSessions(
        Dictionary<int, string> actorSessionIds,
        IReadOnlySet<int> activeActorIds,
        List<int> staleActorIds)
    {
        CollectStaleActorIds(actorSessionIds, activeActorIds, staleActorIds);
        for (int i = 0; i < staleActorIds.Count; i++)
        {
            actorSessionIds.Remove(staleActorIds[i]);
        }
    }

    // Disposes and removes the model handle and forgets the clip of each reassigned actor, so the
    // player now holding the id requests its own instance and starts from its own clip. Returns
    // how many handles were released.
    public static int ReleaseReassignedEntries<THandle>(
        IReadOnlyList<int> reassignedActorIds,
        Dictionary<int, THandle> cachedModels,
        PlayerAnimationClipTracker clipTracker)
        where THandle : IDisposable
    {
        int released = 0;
        for (int i = 0; i < reassignedActorIds.Count; i++)
        {
            int actorId = reassignedActorIds[i];
            clipTracker.Remove(actorId);
            if (cachedModels.Remove(actorId, out THandle? handle))
            {
                handle.Dispose();
                released++;
            }
        }

        return released;
    }

    // Fills farActorIds with the active actors that hold a model handle but are beyond
    // ModelReleaseDistance. The caller disposes and removes them afterwards.
    public static void CollectFarModelActorIds<THandle>(
        Vector3 cameraPosition,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers,
        Dictionary<int, THandle> cachedModels,
        List<int> farActorIds)
    {
        farActorIds.Clear();
        const float releaseDistanceSquared = ModelReleaseDistance * ModelReleaseDistance;
        for (int i = 0; i < remotePlayers.Count; i++)
        {
            RemotePlayerPresentationState remotePlayer = remotePlayers[i];
            if (cachedModels.ContainsKey(remotePlayer.ActorId) &&
                (remotePlayer.Pose.Position - cameraPosition).LengthSquared > releaseDistanceSquared)
            {
                farActorIds.Add(remotePlayer.ActorId);
            }
        }
    }

    // Fills requestActorIds with up to freeSlots actors that have no model handle and are within
    // ModelRequestDistance. Actors already within render distance come first, so a capped frame
    // spends its slots on players that are on screen before the ones in the margin.
    public static void CollectModelRequestActorIds<THandle>(
        Vector3 cameraPosition,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers,
        Dictionary<int, THandle> cachedModels,
        int freeSlots,
        List<int> requestActorIds)
    {
        requestActorIds.Clear();
        const float renderDistanceSquared = WorldVisibility.MaxRenderDistance * WorldVisibility.MaxRenderDistance;
        const float requestDistanceSquared = ModelRequestDistance * ModelRequestDistance;
        for (int pass = 0; pass < 2; pass++)
        {
            float minDistanceSquared = pass == 0 ? -1f : renderDistanceSquared;
            float maxDistanceSquared = pass == 0 ? renderDistanceSquared : requestDistanceSquared;
            for (int i = 0; i < remotePlayers.Count; i++)
            {
                if (requestActorIds.Count >= freeSlots)
                {
                    return;
                }

                RemotePlayerPresentationState remotePlayer = remotePlayers[i];
                float distanceSquared = (remotePlayer.Pose.Position - cameraPosition).LengthSquared;
                if (distanceSquared <= minDistanceSquared ||
                    distanceSquared > maxDistanceSquared ||
                    cachedModels.ContainsKey(remotePlayer.ActorId))
                {
                    continue;
                }

                requestActorIds.Add(remotePlayer.ActorId);
            }
        }
    }

    public static void CountModelStates(
        Dictionary<int, AnimatedModelInstanceHandle> cachedModels,
        out int ready,
        out int pending)
    {
        ready = 0;
        pending = 0;
        foreach (AnimatedModelInstanceHandle handle in cachedModels.Values)
        {
            AsyncResourceState state = handle.State;
            if (state == AsyncResourceState.Ready)
            {
                ready++;
            }
            else if (state == AsyncResourceState.Pending)
            {
                pending++;
            }
        }
    }

    private void ReleaseReassignedActors(IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        CollectReassignedActorIds(remotePlayers, _actorSessionIds, _reassignedActorIds);
        if (_reassignedActorIds.Count == 0)
        {
            return;
        }

        ReleaseReassignedEntries(_reassignedActorIds, _playerModels, _clipTracker);
        for (int i = 0; i < _reassignedActorIds.Count; i++)
        {
            NetworkDiagnosticsLog.Write("client.render.remote.model", $"reassigned actor={_reassignedActorIds[i]} cached={_playerModels.Count}");
        }
    }

    private void ReleaseFarModels(Vector3 cameraPosition, IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        CollectFarModelActorIds(cameraPosition, remotePlayers, _playerModels, _modelActorIds);
        for (int i = 0; i < _modelActorIds.Count; i++)
        {
            int actorId = _modelActorIds[i];
            AnimatedModelInstanceHandle handle = _playerModels[actorId];
            _playerModels.Remove(actorId);
            handle.Dispose();
            NetworkDiagnosticsLog.Write("client.render.remote.model", $"released far actor={actorId} cached={_playerModels.Count}");
        }
    }

    // Requests an instance handle for each actor within ModelRequestDistance that has none, while
    // fewer than MaxPendingModelRequests handles are still waiting for the shared model.
    private void RequestMissingModels(Vector3 cameraPosition, IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        CountModelStates(_playerModels, out _, out int pending);
        int freeSlots = MaxPendingModelRequests - pending;
        if (freeSlots <= 0)
        {
            return;
        }

        CollectModelRequestActorIds(cameraPosition, remotePlayers, _playerModels, freeSlots, _modelActorIds);
        for (int i = 0; i < _modelActorIds.Count; i++)
        {
            int actorId = _modelActorIds[i];
            _playerModels[actorId] = ModelAssets.RequestPlayerInstance();
            NetworkDiagnosticsLog.Write("client.render.remote.model", $"requested actor={actorId} cached={_playerModels.Count}");
        }
    }

    // Disposes the instance handles that are still cached; pruning and far release already
    // disposed the others. The handles are per-instance requests, so this renderer owns them;
    // disposing a handle drops its instance (the shared player model stays the loader's). A second
    // call finds the cache empty, and disposing a handle after the loader's teardown is a no-op.
    public void Dispose()
    {
        _activeActorIds.Clear();
        _actorSessionIds.Clear();

        List<Exception>? failures = null;
        foreach (AnimatedModelInstanceHandle model in _playerModels.Values)
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
        PruneStaleSessions(_actorSessionIds, _activeActorIds, _staleActorIds);

        PruneStaleEntries(_playerModels, _activeActorIds, _staleActorIds);
        for (int i = 0; i < _staleActorIds.Count; i++)
        {
            NetworkDiagnosticsLog.Write("client.render.remote.model", $"pruned actor={_staleActorIds[i]} cached={_playerModels.Count}");
        }
    }
}
