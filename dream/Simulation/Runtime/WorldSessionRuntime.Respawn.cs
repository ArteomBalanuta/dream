using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Runtime;

// Respawn (wave I): a remote player at 0 HP waits PlayerRespawnRules.DelayTicks server ticks and
// returns at the world spawn with full health. Nothing is persisted for the wait: current_hp = 0 is the
// persisted state, and a character that joins dead gets a fresh wait. The timer lives on the session
// (ConnectedPlayer.RespawnAtTick), because the primary Player is reused by successive sessions.
public sealed partial class WorldSessionRuntime
{
    // One scan per gameplay tick, after the enemy step: the primary seat, then the secondary seats in
    // ordinal session-id order. A live player costs one property read and a dead one with a running
    // timer a session lookup. The local seat is never respawned: an empty server's parked seat behaves
    // as it always did.
    private void AdvanceRespawns()
    {
        if (!Player.Combatant.Health.IsAlive)
        {
            PlayerSessionId primarySessionId = ResolvePrimaryControlledSessionId();
            if (primarySessionId != LocalPlayerSessionId)
            {
                AdvanceSeatRespawn(GetPrimaryAuthoritativePlayerTarget(primarySessionId));
            }
        }

        RemoteSessionPlayerState[] secondaries = _orderedSecondaryRemotePlayers;
        for (int i = 0; i < secondaries.Length; i++)
        {
            RemoteSessionPlayerState seat = secondaries[i];
            if (!seat.Combatant.Health.IsAlive)
            {
                AdvanceSeatRespawn(seat);
            }
        }
    }

    // The seat's player is dead. Starts the wait the first tick it is seen dead, respawns it once due
    // (a timer already past due respawns at once).
    private void AdvanceSeatRespawn(IAuthoritativePlayerTarget seat)
    {
        if (!_sessions.TryGet(seat.SessionId, out ConnectedPlayer? player)
            || player == null
            || player.IsLocal
            || !player.IsSpawned)
        {
            return;
        }

        ulong tick = CurrentServerTick;
        if (player.RespawnAtTick == 0)
        {
            player.RespawnAtTick = tick + PlayerRespawnRules.DelayTicks;
            return;
        }

        if (tick < player.RespawnAtTick)
        {
            return;
        }

        // The maximum at this moment: gear equipped while dead counts. Facing stays.
        int maxHealth = seat.Combatant.Health.MaxHealth;
        seat.Combatant.Health.SetAuthoritativeState(maxHealth, maxHealth);
        seat.SetWorldPosition(ResolveRespawnPosition(Map, _worldSpawn));
        player.RespawnAtTick = 0;
    }

    // The world spawn, lifted one cell at a time while a cell the body overlaps, or the one above it, holds a solid cube.
    // The body is one cell wide and centered on X/Z, so at a whole-number X/Z it overlaps two cells on each axis.
    // The lift is capped at the map's height; X and Z clamp to the map. Pure and deterministic, and run at
    // respawn, so a rebuilt map re-clamps. A column solid to the top does not throw: the player lands at the cap.
    internal static Vector3 ResolveRespawnPosition(Map map, Vector3 spawn)
    {
        float x = Math.Clamp(spawn.X, 0.5f, map.Width - 0.5f);
        float z = Math.Clamp(spawn.Z, 0.5f, map.Depth - 0.5f);
        float y = Math.Clamp(spawn.Y, 0.5f, map.Height - 0.5f);
        const float halfBody = 0.5f;
        const float epsilon = 0.001f;
        int minX = (int)MathF.Floor(x - halfBody + epsilon);
        int maxX = (int)MathF.Floor(x + halfBody - epsilon);
        int minZ = (int)MathF.Floor(z - halfBody + epsilon);
        int maxZ = (int)MathF.Floor(z + halfBody - epsilon);
        // A fractured cube leaves no Cube in its cell: its rubble pieces are separate bodies that can lie
        // anywhere (they move), so they are tested by their own boxes, not by cell.
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> rubble = [];
        map.CaptureFracturePiecesInto(rubble);
        while (y < map.Height - 0.5f)
        {
            int cellY = (int)MathF.Floor(y);
            if (!AnySolidCell(map, minX, maxX, cellY, minZ, maxZ)
                && !AnySolidCell(map, minX, maxX, cellY + 1, minZ, maxZ)
                && !AnyPieceInBody(rubble, x, z, cellY, halfBody - epsilon))
            {
                break;
            }

            y += 1f;
        }

        return new Vector3(x, Math.Min(y, map.Height - 0.5f), z);
    }

    // The session's live player target (the primary seat's or a secondary's); tests drive health and position through it.
    internal bool TryGetSessionPlayerTarget(PlayerSessionId sessionId, out IAuthoritativePlayerTarget? target)
        => TryResolveAuthoritativePlayerTarget(sessionId, out _, out target);

    private static bool AnySolidCell(Map map, int minX, int maxX, int y, int minZ, int maxZ)
    {
        for (int cx = minX; cx <= maxX; cx++)
        {
            for (int cz = minZ; cz <= maxZ; cz++)
            {
                if (IsSolidCell(map, cx, y, cz))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The body spans two cells in height (cellY and the one above), like the cube test.
    private static bool AnyPieceInBody(
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> pieces,
        float x,
        float z,
        int cellY,
        float halfBody)
    {
        for (int i = 0; i < pieces.Count; i++)
        {
            GeometryFracturePiece piece = pieces[i].Piece;
            float half = piece.SideLength * 0.5f;
            if (MathF.Abs(piece.Center.X - x) < halfBody + half
                && MathF.Abs(piece.Center.Z - z) < halfBody + half
                && piece.Center.Y + half > cellY
                && piece.Center.Y - half < cellY + 2)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSolidCell(Map map, int x, int y, int z)
        => map.TryGetCubeAtCell(x, y, z)?.IsSolid == true;
}
