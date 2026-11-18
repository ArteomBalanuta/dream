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
        seat.Combatant.SetMana(seat.Combatant.MaxMana);
        player.VitalsDirty = true;
        seat.SetWorldPosition(ResolveRespawnPosition(Map, _worldSpawn, _respawnRubbleScratch));
        player.RespawnAtTick = 0;
        // Enemy steps of ticks tick+1 .. tick+SpawnProtectionTicks skip this player.
        GrantSpawnProtection(player, tick, joinGrantTick: 0UL);
    }

    // BH-PVP-1. Spawn protection is tracked against the CHARACTER as well as the seat, because the seat is
    // not a stable identity: a join by the same account takes the seat over, which detaches and re-attaches
    // in a single tick, and the replacement join may even arrive under a different session id, so a
    // brand-new ConnectedPlayer with no history of its own. Keyed on the seat alone, a player losing a fight
    // could drop the connection and come back with a fresh 3 s of total immunity to PvP and to enemies, land
    // a free hit, and repeat for as long as they liked.
    //
    // Two rules do it, both measured in PlayerRespawnRules.JoinHoldReattachCooldownTicks, the brake the join
    // hold already uses against this same attack:
    //  - a character that spent its window by ATTACKING within the cooldown gets nothing back for rejoining;
    //  - otherwise a join may start a new window only once the cooldown has run since the last join granted
    //    one, and in between it RESUMES what the character has left rather than arming a new one.
    // A respawn still grants a full window and clears the join brake with it: dying is the legitimate way
    // back into the world, and WolfBiteAdversarialTests pins that a rejoin after one is protected in its own
    // right. So a genuine first join, a respawn and a reconnect by someone who was not fighting are all
    // protected exactly as before, while the exploit -- attack, reconnect, attack again, for ever -- is shut,
    // and a player who never attacks still cannot hold immunity by reconnecting faster than it expires.
    // Everything here is in server ticks, so lockstep peers agree.
    private readonly Dictionary<string, CharacterSpawnProtection> _characterSpawnProtection = new(StringComparer.Ordinal);

    // JoinGrantTick: the tick a join last granted this character a window, 0 for none (a respawn clears it).
    // ProtectedThroughTick: the last tick that window covers, 0 once an attack spent it. SpentTick: the tick
    // an attack last spent it, 0 for never.
    private readonly record struct CharacterSpawnProtection(ulong JoinGrantTick, ulong ProtectedThroughTick, ulong SpentTick);

    // An entry older than the cooldown grants exactly what no entry grants, so the table is pruned of those
    // once it grows past this. The removed set is a function of the tick and the contents, never of order.
    private const int CharacterSpawnProtectionCap = 512;

    // A respawn, or a join hold released by the player's first input: a legitimate new window. A respawn
    // clears the join brake (joinGrant = 0); the hold's release is the join's own window, so it keeps it.
    private void GrantSpawnProtection(ConnectedPlayer player, ulong tick, ulong joinGrantTick)
    {
        player.ProtectedThroughTick = tick + PlayerRespawnRules.SpawnProtectionTicks;
        PruneCharacterSpawnProtection(tick);
        _characterSpawnProtection[player.CharacterKey.Value] =
            new CharacterSpawnProtection(joinGrantTick, player.ProtectedThroughTick, SpentTick: 0);
    }

    private ulong CharacterJoinGrantTick(ConnectedPlayer player)
        => _characterSpawnProtection.TryGetValue(player.CharacterKey.Value, out CharacterSpawnProtection prior)
            ? prior.JoinGrantTick
            : 0UL;

    // The player attacked: protection ends, and the character is marked as having been in combat at this
    // tick, so a reconnect inside the cooldown cannot hand it back.
    private void SpendSpawnProtection(ConnectedPlayer player)
    {
        player.ProtectedThroughTick = 0;
        ulong tick = Math.Max(1UL, CurrentServerTick);
        string key = player.CharacterKey.Value;
        CharacterSpawnProtection prior = _characterSpawnProtection.TryGetValue(key, out CharacterSpawnProtection found)
            ? found
            : new CharacterSpawnProtection(0UL, 0UL, 0UL);
        _characterSpawnProtection[key] = prior with { ProtectedThroughTick = 0UL, SpentTick = tick };
    }

    // The window a join grants (AttachRemoteSession, on a fresh attach): resumed, or new once the cooldown ran.
    private void ApplyJoinSpawnProtection(ConnectedPlayer attached)
    {
        ulong tick = CurrentServerTick;
        const ulong cooldown = PlayerRespawnRules.JoinHoldReattachCooldownTicks;
        if (_characterSpawnProtection.TryGetValue(attached.CharacterKey.Value, out CharacterSpawnProtection prior)
            && ((prior.JoinGrantTick != 0 && tick < prior.JoinGrantTick + cooldown)
                || (prior.SpentTick != 0 && tick < prior.SpentTick + cooldown)))
        {
            // Resume, never re-arm. A second live seat on the same character (wave 16: two seats can share a
            // CharacterKey) resumes the same window too, so it cannot be used to fetch a second one.
            attached.ProtectedThroughTick = prior.ProtectedThroughTick >= tick ? prior.ProtectedThroughTick : 0UL;
            return;
        }

        GrantSpawnProtection(attached, tick, joinGrantTick: Math.Max(1UL, tick));
    }

    private void PruneCharacterSpawnProtection(ulong tick)
    {
        if (_characterSpawnProtection.Count < CharacterSpawnProtectionCap)
        {
            return;
        }

        const ulong cooldown = PlayerRespawnRules.JoinHoldReattachCooldownTicks;
        List<string>? stale = null;
        foreach (KeyValuePair<string, CharacterSpawnProtection> entry in _characterSpawnProtection)
        {
            if ((entry.Value.JoinGrantTick == 0 || tick >= entry.Value.JoinGrantTick + cooldown)
                && tick > entry.Value.ProtectedThroughTick
                && (entry.Value.SpentTick == 0 || tick >= entry.Value.SpentTick + cooldown))
            {
                (stale ??= []).Add(entry.Key);
            }
        }

        if (stale == null)
        {
            return;
        }

        for (int i = 0; i < stale.Count; i++)
        {
            _characterSpawnProtection.Remove(stale[i]);
        }
    }

    // The normal window, or a join's opt-in hold that no input has released yet. The hold is capped: if
    // JoinHoldMaxTicks pass without input, protection simply ends (the player is AFK) and a later first
    // input grants nothing, so a stale prompt cannot be used to sit untargetable in combat.
    private static bool IsSpawnProtected(ConnectedPlayer player, ulong tick) =>
        (player.ProtectedThroughTick != 0 && player.ProtectedThroughTick >= tick)
        || (player.ProtectionHeldSinceTick != 0
            && tick <= player.ProtectionHeldSinceTick + PlayerRespawnRules.JoinHoldMaxTicks);

    // Only honoured for the join that freshly attached the seat: a repeated join or a takeover of an
    // attached seat must not re-arm it.
    //
    // BH-REV1. FreshlyAttached is a property of the SEAT, and the client picks its own session id, so a
    // rejoin under a brand-new id always had it: that walked straight past the character brake above, and
    // the hold's release then wrote a full fresh window and a cleared spent mark into the character record.
    // A hold only ever defers the window THIS join granted, so it is gated on the same character record
    // ApplyJoinSpawnProtection consults: armed only when that join actually granted a new window, i.e. the
    // record's JoinGrantTick is this seat's own fresh attach. A join that merely resumed -- the character
    // attacked, or rejoined inside JoinHoldReattachCooldownTicks -- has nothing to defer and gets no hold,
    // exactly as a reconnect inside the cooldown already got none under its old session id.
    public void HoldSpawnProtectionUntilInput(PlayerSessionId sessionId)
    {
        if (_sessions.TryGet(sessionId, out ConnectedPlayer? player) && player != null && player.IsSpawned
            && player.FreshlyAttached)
        {
            ulong joinGrantTick = CharacterJoinGrantTick(player);
            if (joinGrantTick == 0 || joinGrantTick < player.LastFreshAttachTick)
            {
                return;
            }

            player.ProtectionHeldSinceTick = Math.Max(1UL, CurrentServerTick);
        }
    }

    // The first input within the cap ends the hold and starts the normal window; past the cap the hold has
    // already ended and nothing is granted.
    private void ReleaseSpawnProtectionHold(ConnectedPlayer player)
    {
        if (player.ProtectionHeldSinceTick == 0)
        {
            return;
        }

        ulong tick = CurrentServerTick;
        bool stillHeld = tick <= player.ProtectionHeldSinceTick + PlayerRespawnRules.JoinHoldMaxTicks;
        player.ProtectionHeldSinceTick = 0;
        if (!stillHeld)
        {
            return;
        }

        // BH-REV1, second half of the same brake: whatever armed the hold, the release may not hand back a
        // window the character spent by attacking, nor erase the spent mark that refuses the next rejoin.
        const ulong cooldown = PlayerRespawnRules.JoinHoldReattachCooldownTicks;
        if (_characterSpawnProtection.TryGetValue(player.CharacterKey.Value, out CharacterSpawnProtection prior)
            && prior.SpentTick != 0 && tick < prior.SpentTick + cooldown)
        {
            // Resume what is left (nothing, once an attack spent it), exactly as a rejoin does.
            player.ProtectedThroughTick = prior.ProtectedThroughTick >= tick ? prior.ProtectedThroughTick : 0UL;
            return;
        }

        GrantSpawnProtection(player, tick, CharacterJoinGrantTick(player));
    }

    private void ReleaseSpawnProtectionHold(PlayerCommandFrame frame, ConnectedPlayer? player)
    {
        if (player != null
            && player.ProtectionHeldSinceTick != 0
            && (frame.MoveIntent.ForwardAxis != 0f || frame.MoveIntent.StrafeAxis != 0f
                || frame.MoveIntent.VerticalAxis != 0f || frame.MoveIntent.JumpPressed || frame.PrimaryUsePressed))
        {
            ReleaseSpawnProtectionHold(player);
        }
    }

    // The tick's players minus those under spawn protection: what enemies target and bite. Reused list,
    // no allocation. Separation still uses the full list, so a protected body stays solid.
    private void FillEnemyTargets(List<IAuthoritativePlayerTarget> all, List<IAuthoritativePlayerTarget> targets)
    {
        targets.Clear();
        ulong tick = CurrentServerTick;
        for (int i = 0; i < all.Count; i++)
        {
            IAuthoritativePlayerTarget target = all[i];
            if (_sessions.TryGet(target.SessionId, out ConnectedPlayer? player)
                && player != null
                && IsSpawnProtected(player, tick))
            {
                continue;
            }

            targets.Add(target);
        }
    }

    private readonly List<IAuthoritativePlayerTarget> _enemyTargets = [];

    // Reused by every respawn (the tick lock serializes them); cleared and refilled each time.
    private readonly List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> _respawnRubbleScratch = [];

    // The world spawn, lifted one cell at a time while the body box holds a solid cube or a rubble piece. The
    // body is one cell wide and centered on X/Z, so at a whole-number X/Z it overlaps two cells on each axis, and
    // it is tested over its own cell and the one above. Rubble (the pieces of a fractured cube, which leave no
    // cube in their cell) is tested by the pieces' own boxes. The lift is capped at the map's height; X and Z
    // clamp to the map. Pure and deterministic, and run at respawn, so a rebuilt map re-clamps. A column solid to
    // the top does not throw: the player lands at the cap. Pass a scratch list to avoid one allocation per call.
    internal static Vector3 ResolveRespawnPosition(
        Map map,
        Vector3 spawn,
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)>? scratch = null)
    {
        float x = Math.Clamp(spawn.X, 0.5f, map.Width - 0.5f);
        float z = Math.Clamp(spawn.Z, 0.5f, map.Depth - 0.5f);
        float y = Math.Clamp(spawn.Y, 0.5f, map.Height - 0.5f);
        // A fractured cube leaves no Cube in its cell: its rubble pieces are separate bodies that can lie
        // anywhere (they move), so they are tested by their own boxes, not by cell.
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> rubble = scratch ?? [];
        rubble.Clear();
        map.CaptureFracturePiecesInto(rubble);
        while (y < map.Height - 0.5f)
        {
            if (BodyFits(map, rubble, x, z, (int)MathF.Floor(y)))
            {
                break;
            }

            y += 1f;
        }

        y = Math.Min(y, map.Height - 0.5f);
        int bodyCell = (int)MathF.Floor(y);
        // BH-PVP-2: the lift only escapes upwards and stops the moment the spawn's own cells are clear, so a
        // ring of cubes around the spawn cell with a roof over it left every respawning player sealed in --
        // and the cubes are their builder's (CanDamageCube), so the victim could never break out and a relog
        // put them back in the same box. If the body cannot reach the edge of the search area from here, step
        // to the nearest free cell that the sealed region does not contain. Columns are tried in a fixed ring
        // and ordinal order and nothing but the Map is read, so every lockstep peer picks the same cell. If
        // the lift hit the map's cap, or nothing free is in reach, this is the position the lift gave.
        if (BodyFits(map, rubble, x, z, bodyCell))
        {
            return ResolveRespawnEscape(map, rubble, x, y, z, bodyCell);
        }

        return new Vector3(x, y, z);
    }

    // How far around the spawn a sealed-in player is searched for a way out, in cells, and how many cells of
    // climb or drop the search may use on the way (a step or a short jump, so natural ground counts as open).
    private const int RespawnEscapeRadius = 8;
    private const int RespawnEscapeClimb = 3;
    private const int RespawnEscapeSide = (2 * RespawnEscapeRadius) + 1;
    private const int RespawnEscapeLevels = (2 * RespawnEscapeClimb) + 1;

    private static Vector3 ResolveRespawnEscape(
        Map map,
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> rubble,
        float x,
        float y,
        float z,
        int bodyCell)
    {
        bool[] visited = new bool[RespawnEscapeSide * RespawnEscapeSide * RespawnEscapeLevels];
        bool[] sealedColumn = new bool[RespawnEscapeSide * RespawnEscapeSide];
        int[] queue = new int[RespawnEscapeSide * RespawnEscapeSide * RespawnEscapeLevels];
        int head = 0;
        int tail = 0;
        int start = NodeIndex(0, 0, 0);
        visited[start] = true;
        queue[tail++] = start;
        sealedColumn[ColumnIndex(0, 0)] = true;
        while (head < tail)
        {
            int node = queue[head++];
            int level = (node % RespawnEscapeLevels) - RespawnEscapeClimb;
            int column = node / RespawnEscapeLevels;
            int dx = (column / RespawnEscapeSide) - RespawnEscapeRadius;
            int dz = (column % RespawnEscapeSide) - RespawnEscapeRadius;
            if (Math.Abs(dx) == RespawnEscapeRadius || Math.Abs(dz) == RespawnEscapeRadius)
            {
                // Reached the edge of the search area: the player is not sealed in.
                return new Vector3(x, y, z);
            }

            for (int step = 0; step < 4; step++)
            {
                int ndx = dx + (step == 0 ? 1 : step == 1 ? -1 : 0);
                int ndz = dz + (step == 2 ? 1 : step == 3 ? -1 : 0);
                float nx = x + ndx;
                float nz = z + ndz;
                if (nx < 0.5f || nx > map.Width - 0.5f || nz < 0.5f || nz > map.Depth - 0.5f)
                {
                    // The open edge of the map is a way out too.
                    return new Vector3(x, y, z);
                }

                for (int nlevel = level - 1; nlevel <= level + 1; nlevel++)
                {
                    if (nlevel < -RespawnEscapeClimb || nlevel > RespawnEscapeClimb)
                    {
                        continue;
                    }

                    int next = NodeIndex(ndx, ndz, nlevel);
                    if (visited[next] || !BodyFits(map, rubble, nx, nz, bodyCell + nlevel))
                    {
                        continue;
                    }

                    visited[next] = true;
                    queue[tail++] = next;
                    sealedColumn[ColumnIndex(ndx, ndz)] = true;
                }
            }
        }

        // Sealed in. The nearest cell outside the sealed region that the body fits in at the spawn's own
        // level, by ring then ordinal order.
        for (int ring = 1; ring <= RespawnEscapeRadius; ring++)
        {
            for (int dx = -ring; dx <= ring; dx++)
            {
                for (int dz = -ring; dz <= ring; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring || sealedColumn[ColumnIndex(dx, dz)])
                    {
                        continue;
                    }

                    float nx = x + dx;
                    float nz = z + dz;
                    if (nx < 0.5f || nx > map.Width - 0.5f || nz < 0.5f || nz > map.Depth - 0.5f
                        || !BodyFits(map, rubble, nx, nz, bodyCell))
                    {
                        continue;
                    }

                    return new Vector3(nx, y, nz);
                }
            }
        }

        return new Vector3(x, y, z);

        static int ColumnIndex(int dx, int dz)
            => ((dx + RespawnEscapeRadius) * RespawnEscapeSide) + dz + RespawnEscapeRadius;

        static int NodeIndex(int dx, int dz, int level)
            => (ColumnIndex(dx, dz) * RespawnEscapeLevels) + level + RespawnEscapeClimb;
    }

    // The one-cell-wide, two-cell-tall body standing on cellY, against cubes and against rubble.
    private static bool BodyFits(
        Map map,
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> rubble,
        float x,
        float z,
        int cellY)
    {
        const float halfBody = 0.5f;
        const float epsilon = 0.001f;
        int minX = (int)MathF.Floor(x - halfBody + epsilon);
        int maxX = (int)MathF.Floor(x + halfBody - epsilon);
        int minZ = (int)MathF.Floor(z - halfBody + epsilon);
        int maxZ = (int)MathF.Floor(z + halfBody - epsilon);
        return !AnySolidCell(map, minX, maxX, cellY, minZ, maxZ)
            && !AnySolidCell(map, minX, maxX, cellY + 1, minZ, maxZ)
            && !AnyPieceInBody(rubble, x, z, cellY, halfBody - epsilon);
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
