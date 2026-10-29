# Persistence Wave E Plan: Hosting Stored Worlds

Purpose:
- A world whose terrain is stored chunks can be hosted.
- Clients receive that terrain at join (protocol v8) and build their map from it.
- A world's terrain can be exported to a new stored world, and reset.
- Edits on stored worlds behave exactly as on generated worlds.
- A soak test covers fractures and restarts on a stored world.

Status: accepted with the recommended defaults for U7, U8, U9, U2 and U10. The user may still object. Analysed read-only on `integrate/engine-iteration` at `f87bf94`. Standing user decisions: no offline time, and no generator work; terrain stays as wave D left it, the default generator plus stored chunks.

## Decisions

| Id | Decision |
| --- | --- |
| E1 | Stored terrain travels inside the join's world transfer, as `server.terrain-chunk` frames. They come after the carrier and before world chunk 0. There is no separate channel and no change to the join handshake. |
| E2 | The server sends the world's stored base chunks byte-for-byte as loaded. They are not re-exported. Removals (overrides) reach the client through the join snapshot, as on generated worlds. The payload is built once per host, never on a tick. |
| E3 | Transfer is JSON with base64 (`Utf8JsonWriter.WriteBase64String` / `TryGetBytesFromBase64`): one codec, and the 4/3 overhead fits the 288 KB budget. |
| E4 | Clients do not cache terrain by digest. A reconnect downloads it again (about 290 KB for World10k). |
| E5 | Stored worlds keep the hosting limits: square, 5–100, height 8. On a stored world, `generator_key` and `generator_version` only record where the terrain came from; neither client nor server checks them against the catalog. |
| E6 | No migration. Schema v6 already has `terrain_chunks` and `map_source IN ('generated','stored')`. |
| E7 | The protocol goes to v8 for every world. An unedited generated world's join stays byte-identical to C0, because the manifest is null and omitted. |
| U7 (user, default applied) | **Export always creates a new world:** `--worlds export <src> <new name> [--terrain-only]`. There is no in-place bake. To turn a world into a stored one, export it and delete the old one. |
| U8 (user, default applied) | **Reset-terrain restores the world's own base**: the generator output for a generated world, its own chunks for a stored world. Cells that hold a saved placed cube are left alone (their override and rubble stay) and reported. |
| U9 (user, default applied) | `--terrain-only` on export: a fresh world with the chunks only, a new seed and world time 0. It is cheap and gives map templates. |
| U2 (user, default applied) | Persisting terrain health stays out: standing damaged terrain still heals. It needs a new override kind or table (migration 0007), a dirty set on the per-hit hot path, and new heal semantics, and correctness does not depend on it. |
| U10 (user, default applied) | Export and reset are offline commands. There is no lease that detects a running server; the rule is documented and printed. A hosting lease belongs to Phase 5. |
| E8 | `RepairCubeRecordFromPersistence` cell matching (a wave C limit) stays out. It is a fallback for a lost registry link with no reported bug, and changing it touches the combat and persistence hot path. Wave E adds no new path into it, because terrain cubes have no persisted placed-cube row. |

## What The Code Does Today

**Hosting.**
- `WorldDescriptor.Validate` rejects `MapSource != Generated`. `ServerBootstrap.CreatePersistenceBundle` calls it, so opening a stored world exits with code 5.
- `MapSources.ForWorld` throws `NotSupportedException` for `Stored`.
- `WorldSessionRuntime.CreateMap` uses `new Map(MapSources.ForWorld(world, SimulationSeed), Physics, headless)`.

**Stored terrain.**
- `StoredMapSource(MapDefinition, chunks)` checks coverage, format v1 and duplicates, and decodes fresh records in canonical (x, z, y) order on every `CreateCells`.
- `TerrainChunkCodec.Decode` length-checks every read, but has no budget on decoded subcell overrides. Resolution can be up to 40, so up to 64,000 dictionary entries per cell.
- `TerrainChunkExporter.Export(Map)` exports the source cells still standing plus the y == 0 floor.
- `ServerPersistenceBundle.TerrainChunks` exposes `ITerrainChunkRepository`, but nothing calls it at startup.
- World10k is 49 chunks, 215,996 B raw.

**Join.**
- `RuntimeAuthoritativeSessionHost.JoinSession` builds a `JoinAcceptedMessage` with `InitialWorld` and `World: WorldDescriptorWire`.
- `ServerConnection.StartWorldTransferAsync` turns it into a carrier (`WorldTransferId`) plus `server.world-chunk` frames of about 64 KB. These alternate with queued messages.
- On the client, `WorldTransferReassembler` holds the carrier and every message after it until the final world chunk. It is bounded by `WorldTransferLimits`.

**Client map.**
- `ClientJoinAcceptedApplier.ApplyWorldDescriptor` rejects unknown generators and `Validate` failures (`UnsupportedWorldException`), then calls `RebuildMapForWorld` (which goes through `RebuildMapForFloorSize` and `CopyPlacedCubesTo`) or `AdoptWorldDescriptor`. It always builds a generated map.
- `RemoteWorldMutationApplier.ApplyFullWorldSnapshot` rebuilds the map when snapshot dimensions differ (line ~201).
- `ApplyWorldSnapshot` removes raised cubes the snapshot does not list, and re-adds authored cells through `Map.RestoreGeneratedStartupCells`, which reads `Source`. So the snapshot cannot describe base geometry; the client must have the base.

**Edits.**
- Overrides are keyed by cell and use `Map.IsSourceCell` / `RemoveSourceCell`, which do not depend on the source.
- On restore, a kept rubble group whose cell still stands, and whose origin is neither an override nor a persisted placed cube, is "healed": the cell is removed again (`RuntimePersistenceBridge`, `PrepareRubbleGroups`).

**CLI.**
- `WorldCli`: `list | create | copy | delete`. The list format is `id, name, WxHxD, key:version, ticks`.
- `SqliteWorldCatalog.Copy` copies overrides (same ids) and chunks.

## Scope

**In:**
- Hosting stored worlds.
- Protocol v8 terrain transfer, and the client building its map from it.
- Export CLI.
- Reset-terrain CLI.
- Edits on stored worlds, verified by tests.
- Soak and measurements.
- Docs.

**Out:**
- Generators (`IMapGenerator` work).
- Offline time.
- Terrain health (U2).
- Cell-matching repair (E8).
- `Replaced` overrides (still logged and skipped).
- A client terrain cache.
- A live (online) reset.
- A hosting lease.
- Resizing stored worlds: `RebuildMapForFloorSize` throws for them.

## Determinism Rules

1. Server and client build a stored map from the same bytes through `StoredMapSource`, so cell order and physics-body order are identical. This is stronger than on generated worlds.
2. Overrides on stored worlds use the wave D path unchanged: `RemoveSourceCell` in `object_id` order, before cubes. Two servers restarted from one database stay in lockstep.
3. The terrain payload is the loaded rows, unchanged. Its digest is SHA-256 over, for each chunk in (chunk_z, chunk_x) ascending order: `i32 x, i32 z, u16 formatVersion, i32 cellCount, i32 dataLength, data`, all little-endian. The manifest carries it as lowercase hex.
4. Generated worlds send no manifest and no terrain frames. Join bytes equal C0's. Startup goldens, `SimulationStateChecksum`, snapshot-byte and checksum tests stay unchanged. Only C0 may change pinned `protocolVersion` 7 to 8.
5. Export is deterministic: the same database state gives identical blobs (pinned by a test).
6. A reset followed by a restart gives the base map minus the cells that were left alone, and two lockstep servers stay equal.

## C0 Contracts

C0 is one commit on the tip. The suite stays green, and behaviour for generated worlds is unchanged. After C0, nobody edits the contract types except to add what a workstream's section names.

**Protocol bump:** `TransportEnvelope.CurrentProtocolVersion = 8`. Fix every test that pins 7 (`ProtocolFoundationTests`, `TransportJsonCodecDecodeTests`, `JoinAcceptedWorldDescriptorCodecTests`, `ProtocolV2*`, `ProtocolVersionMismatchClientTests`, `WaveBTesterWorldsLoopbackTests`, `WorldSizeMismatchLoopbackTests`, `WorldClockAnchorProtocolTests`, `WaveT2BuildMaterialTests`, `ProtocolV2SlimmingTests`).

**Descriptor and sources:**

```csharp
// WorldDescriptor.Validate: MapSource may be Generated or Stored. The catalog check applies to
// Generated only. Stored needs a non-blank GeneratorKey and GeneratorVersion >= 1 (provenance).

// dream/Simulation/World/MapSources/MapSources.cs
public static IMapSource ForWorld(WorldDescriptor world, ulong seed,
    IReadOnlyList<PersistedTerrainChunk>? storedTerrain = null);
// Generated: storedTerrain must be null (ArgumentException).
// Stored: storedTerrain null -> InvalidOperationException("stored world has no terrain chunks");
//         else new StoredMapSource(MapDefinition.ForWorld(world), storedTerrain).

// TerrainChunkCodec: decode budget (untrusted input)
public static List<GeometryCellRecord> Decode(MapDefinition definition, int chunkX, int chunkZ,
    ReadOnlySpan<byte> data, ref long subcellOverrideBudget);
// Subtracts each override set's entry count BEFORE allocating it. Throws InvalidDataException when
// the budget would drop below 0. The existing overload passes long.MaxValue.

// StoredMapSource
public StoredMapSource(MapDefinition definition, IReadOnlyList<PersistedTerrainChunk> chunks,
    long maxSubcellOverrides = long.MaxValue);   // budget per CreateCells call
```

**Runtime plumbing:**

```csharp
// WorldSessionRuntime (descriptor ctor gets a trailing optional parameter)
WorldSessionRuntime(WorldDescriptor world, ..., bool headlessMap = true,
    IReadOnlyList<PersistedTerrainChunk>? storedTerrain = null);
public IReadOnlyList<PersistedTerrainChunk>? StoredTerrain { get; }   // null for generated worlds
// CreateMap passes StoredTerrain to MapSources.ForWorld.
// RebuildMapForFloorSize throws NotSupportedException on a stored world.

// RuntimePersistencePorts
public IReadOnlyList<PersistedTerrainChunk>? StoredTerrain { get; init; }
// RuntimeAuthoritativeSessionHost.CreateDefault passes it through.

// ServerBootstrap.CreatePersistenceBundle, C0 only: a stored world still exits 5 with
// "stored worlds are hosted from wave E" (B replaces this).
```

**Transfer types** (`dream/Multiplayer/Protocol/`):

```csharp
// TerrainTransferManifest.cs: wire, in server.join-accepted as "terrain"
// {"chunks":49,"fragments":5,"bytes":215996,"sha256":"<64 lowercase hex>"}
public sealed record TerrainTransferManifest(int Chunks, int Fragments, long Bytes, string Sha256);

// TerrainTransferDigest.cs
public static class TerrainTransferDigest
{
    public static string Compute(IReadOnlyList<PersistedTerrainChunk> chunksInZxOrder);   // rule 3
    public static IncrementalHash CreateIncremental();                                     // SHA-256
    public static void Append(IncrementalHash hash, in PersistedTerrainChunk chunk);
}

// TerrainTransferPayload.cs: server side, immutable, shared by every join of one host
public sealed class TerrainTransferPayload
{
    public const int DefaultFragmentRawBytes = 48 * 1024;   // about 64 KB after base64, like world chunks

    // Validates: world.MapSource == Stored; StoredMapSource coverage rules; every blob <=
    // WorldTransferLimits.Default.MaxTerrainBlobBytes; total <= MaxTerrainBytes
    // (InvalidDataException otherwise). Chunks are sorted (chunk_z, chunk_x). Fragments are greedy:
    // whole blobs while the raw sum stays <= fragmentRawBytes, at least one blob each.
    public static TerrainTransferPayload Create(WorldDescriptor world,
        IReadOnlyList<PersistedTerrainChunk> chunks, int fragmentRawBytes = DefaultFragmentRawBytes);
    public IReadOnlyList<PersistedTerrainChunk> Chunks { get; }
    public IReadOnlyList<Range> Fragments { get; }      // index ranges into Chunks
    public TerrainTransferManifest Manifest { get; }
}

// JoinAcceptedMessage: one positional parameter appended, two non-wire properties
//   ..., WorldDescriptorWire? World = null, TerrainTransferManifest? Terrain = null)
[JsonIgnore] public TerrainTransferPayload? TerrainPayload { get; init; }            // server: the writer streams it
[JsonIgnore] public IReadOnlyList<PersistedTerrainChunk>? ReceivedTerrain { get; init; } // client: set by the reassembler

// WorldTransferLimits: new init properties
public long MaxTerrainBytes { get; init; } = 16L << 20;
public int MaxTerrainBlobBytes { get; init; } = 1 << 20;
public long MaxTerrainSubcellOverrides { get; init; } = 1L << 22;
```

**Client interface:**

```csharp
// IClientWorldState
void RebuildMapForWorld(WorldDescriptor world, IMapSource? source = null);
// In C0, ClientWorldSession ignores source == null (today's behaviour) and throws
// NotSupportedException for non-null. B implements it.
// ClientJoinAcceptedApplier (C0): the generator check applies to Generated only; any Stored world
// -> UnsupportedWorldException("stored worlds need a v8 terrain transfer") until B.
```

**C0 tests:**
- `WorldDescriptorContractTests`: stored is now valid, and a blank provenance key is invalid.
- `WorldGeneratorCatalogTests`: update the stored case.
- `MapSourcesStoredTests`: a runtime built with `storedTerrain` exported from (56,8,56) generated has an equal `WorldChecksum` and cell set. Generated plus chunks, and stored without chunks, both throw.
- `TerrainTransferPayloadTests`:
  - a golden digest for a hand-built 2-chunk set;
  - fragment plan cases: exact fit, an oversize single blob, World10k gives ≤ 6 fragments;
  - limit rejections;
  - order independence of the input.
- `TerrainChunkCodecBudgetTests`:
  - the budget is exhausted before allocation;
  - assert, and record, the World10k generated decoded override total. It must be ≤ `MaxTerrainSubcellOverrides` / 4.
- `JoinAcceptedMessage` with `TerrainPayload` and `ReceivedTerrain` set encodes byte-identical to without.
- The client applier rejects stored worlds.

## Wire Format (protocol v8)

The order on one connection for a stored-world join:

```
server.join-accepted   carrier: "worldTransferId":n, "world":{...,"mapSource":"stored",...},
                       "terrain":{"chunks":49,"fragments":5,"bytes":215996,"sha256":"..."}
server.terrain-chunk   {"transferId":n,"index":0,"chunks":[{"x":0,"z":0,"v":1,"n":312,"data":"<base64>"},...]}
  ...                  (queued messages interleave as for world chunks; they are held by the client)
server.terrain-chunk   {"transferId":n,"index":4,"chunks":[...],"final":true}
server.world-chunk     index 0 .. final, unchanged
```

- `index` is per transfer and counts terrain frames only. World-chunk `chunkIndex` still starts at 0.
- Chunks across all fragments are strictly ascending by (z, x).
- `data` is standard base64, written with `WriteBase64String`. Any escaped string is rejected.
- Frame size: at most `ceil(4/3 × max(48 KiB, one blob ≤ 1 MiB))` plus overhead, so ≤ 1.4 MB, under the 8 MB client read cap. Typical frames are about 64 KB.
- Resyncs (`server.snapshot` carriers) never carry terrain.

**Client validation, all as protocol errors that end the session.** Limits throw `WorldTransferLimitException`; order and shape errors throw `InvalidOperationException` / `InvalidDataException`.
- **Carrier with `terrain`.** `world` must be present and `stored`. `chunks` must equal `ceil(W/16) × ceil(D/16)` of `world`. `1 ≤ fragments ≤ chunks`. `0 < bytes ≤ MaxTerrainBytes`. `sha256` must be 64 lowercase hex characters.
- **A terrain frame is an error when:**
  - no transfer is in progress;
  - the carrier has no manifest;
  - the transfer id is wrong;
  - the index is not the next expected one;
  - a world chunk has already arrived;
  - its `chunks` array is empty.
- **Per chunk entry:**
  - `x` and `z` are in the grid, and the entry comes after the previous one in (z, x) order;
  - `v` equals `TerrainChunkFormat.CurrentVersion`;
  - `0 ≤ n ≤ 16·16·H`;
  - the base64 length is pre-checked as ≤ `4·ceil(MaxTerrainBlobBytes/3)` before decoding;
  - the running bytes stay ≤ `manifest.bytes`, and the running chunk count stays ≤ `manifest.chunks`.
- **`final`** is present if and only if `index == fragments - 1`. At `final`, the counts and bytes must be equal, and the incremental digest must match.
- **A world chunk** arriving before the terrain `final` is an error.
- **Decoding the blobs** happens when the join is applied, with `MaxTerrainSubcellOverrides` as the budget. An `InvalidDataException` there becomes a protocol error.

## Workstreams

Rules for every workstream:
- Touch only the files you own.
- The build and the full suite pass. Any skip reads `wave E: needs <X>, unskip at glue` and is listed in the hand-off.
- 0 B per tick.
- Goldens, checksums and snapshot bytes stay unchanged.
- Build with an absolute `BaseOutputPath` inside the worktree, and reset the worktree to the C0 tip first.

### A: Terrain transfer, both ends of the wire

**Owns:**
- `Multiplayer/Protocol/TerrainChunkMessage.cs` (new): the record `TerrainChunkMessage(int TransferId, int Index, bool IsFinal, IReadOnlyList<PersistedTerrainChunk> Chunks)`, `Kind = "server.terrain-chunk"`, and a hand-written converter in the `WorldChunkMessageJsonConverter` style.
- `TransportJsonCodec.cs`: kind registration, decode switch, server-kind list.
- `WorldTransfer.cs`:
  - `WorldTransferSender` gets a terrain prelude: a ctor parameter `TerrainTransferPayload? terrain`, `string NextFrameKind`, and `WriteNextChunk`, which writes terrain fragments first. `IsComplete` only after the world's final chunk.
  - `WorldTransferReceiver` and `WorldTransferReassembler`: accept terrain frames with the validation above, count them as progress for the deadline, and on completion deliver `WithWorld(...) with { ReceivedTerrain = chunks }`.
- `Server/ServerConnection.cs`: `StartWorldTransferAsync` takes `(message as JoinAcceptedMessage)?.TerrainPayload`; `EncodeServerFrame` uses `NextFrameKind`; a `TerrainFragmentsSent` counter.
- `Client/Networking/RemoteClientSession.cs`: terrain frame bytes count toward `_worldTransferBytes` (the HUD progress), plus diagnostics.

**Tests:**
- New `Multiplayer/Protocol/TerrainChunkMessageCodecTests.cs`:
  - a golden wire string;
  - the output contains no `+` and no backslash;
  - escaped, oversize and malformed base64 are rejected.
- New `TerrainTransferReceiverTests.cs`: every validation rule above, one test each.
- A fuzz pass: 500 seeded random byte flips and truncations of real fragments. Each run ends in a protocol exception, never an unhandled or other exception, and memory stays bounded.
- A deadline and progress test that includes terrain frames.
- New `Server/ServerConnectionTerrainTransferTests.cs`:
  - a fake transport sees carrier, terrain 0..F-1, world 0..final, with queued messages interleaved;
  - a carrier without a payload produces exactly today's frame sequence and bytes;
  - a fault mid-terrain releases the capture.

**Acceptance:** a JoinAccepted with a payload round-trips through server writer, frames and reassembler, and `ReceivedTerrain` is equal to the payload chunks byte-for-byte.

**Must not touch:** `Simulation/**`, `ServerBootstrap`, the session host, the client applier and session, the CLI.

### B: Hosting and the client build

**Owns:**
- `Server/ServerBootstrap.cs`:
  - for a stored world, load `new SqliteTerrainChunkRepository(schema, worldId).LoadTerrainChunks()`;
  - check them with `TerrainTransferPayload.Create`;
  - log `World '<name>' (id n) stored terrain: chunks= bytes= sha256=`;
  - any `InvalidDataException` exits 5 with the reason.
- `Server/Persistence/ServerPersistenceBundle.cs`: `StoredTerrain`, passed into the ports.
- `Simulation/Runtime/RuntimeAuthoritativeSessionHost.cs`:
  - build `TerrainTransferPayload` once, in the constructor or `CreateDefault`, from `Runtime.StoredTerrain`;
  - both `JoinSession` paths set `Terrain = payload.Manifest` and `TerrainPayload = payload`;
  - generated worlds set neither.
- `Client/Replication/ClientJoinAcceptedApplier.cs`:
  - Stored: `ReceivedTerrain` is required (`UnsupportedWorldException` "the server sent no terrain" otherwise);
  - build `new StoredMapSource(MapDefinition.ForWorld(d), terrain, WorldTransferLimits.Default.MaxTerrainSubcellOverrides)`;
  - always call `RebuildMapForWorld(d, source)`;
  - an `InvalidDataException` becomes `InvalidDataException("Protocol error: stored terrain ...")`.
- `Client/World/ClientWorldSession.cs`, `RebuildMapForWorld(world, source)`:
  - `Physics.ResetMapBodies()`;
  - `new Map(source, Physics, replicatedCubeBodies: true)`;
  - **no** `CopyPlacedCubesTo`, because it would replay the old map's removals onto a different source, and the snapshot follows immediately;
  - `BindMap`, clamp the player, `World = world`.
- `Client/Replication/RemoteWorldMutationApplier.cs`: a dimension mismatch on a stored world throws `InvalidDataException`, never a generated rebuild.

**Tests:**
- New `Server/StoredWorldHostingTests.cs`:
  - bootstrap opens a stored world (exported in-test through `ReplaceTerrainChunks`), and its runtime checksum equals the generated source's;
  - missing, partial, corrupt and over-limit chunks each exit 5 with a log;
  - joins carry the manifest, and the same payload instance is reused across joins;
  - generated joins have no `terrain`.
- New `Client/StoredWorldJoinApplierTests.cs`, which sets `ReceivedTerrain` directly:
  - the client map's checksum, raised key set and body count equal the server's;
  - a stored world without terrain is rejected;
  - a corrupt blob or over-budget overrides end the join as a protocol error;
  - a resync with other dimensions throws;
  - a generated join behaves exactly as before.

**Must not touch:** the protocol files A owns, `Map*.cs`, the persistence bridge, the CLI and catalog.

### C: Export and reset-terrain CLI

**Owns:** `Server/Worlds/WorldCli.cs`, `IWorldCatalog.cs`, `Server/Persistence/Worlds/SqliteWorldCatalog.cs`, and the new `Server/Worlds/WorldTerrainBaker.cs`, plus every `IWorldCatalog` fake.

```csharp
// WorldTerrainBaker
public static BakedTerrain Bake(WorldRecord source, ulong seed,
    IReadOnlyList<PersistedTerrainChunk>? storedChunks, IReadOnlyList<PersistedTerrainCellOverride> overrides);
// new Map(MapSources.ForWorld(d, seed, storedChunks), physics: null, headless: true);
// Removed overrides -> RemoveSourceCell in object_id order; Replaced -> counted, skipped.
// TerrainChunkExporter.Export(map); TerrainTransferPayload.Create(stored descriptor, chunks)
// validates the hosting limits.
public sealed record BakedTerrain(IReadOnlyList<PersistedTerrainChunk> Chunks, int RemovedCells,
    int SkippedOverrides, long Bytes);

// IWorldCatalog
// One owned transaction: a new world, map_source 'stored', the source's bounds and provenance
// generator, and the chunks.
// Full copy (terrainOnly false): every world-scoped row as Copy does, EXCEPT
// terrain_cell_overrides (baked); same ids, counter, seed and world time.
// terrainOnly: no other rows, a new seed, world time 0, next_object_id 1.
WorldRecord CreateStored(WorldId source, string newName, IReadOnlyList<PersistedTerrainChunk> chunks,
    bool terrainOnly);
// One owned transaction.
// For every override cell that holds no placed_cube_instances row of that world: delete the
// override AND every cube_fracture_pieces row whose parent cell is that cell (any origin, so the
// restore heal cannot remove the cell again). Cells under a placed cube keep their override and
// rubble.
TerrainResetResult ResetTerrain(WorldId id);
public readonly record struct TerrainResetResult(int OverridesCleared, int RubbleRowsDeleted, int CellsKeptForPlacedCubes);
```

**CLI:**
- `export <id|name> <new name> [--terrain-only]` prints the new world's line.
- `reset-terrain <id|name> --yes` prints `reset-terrain\t<id>\t<name>\tcleared=\trubble=\tkept=`.
- Both print the stderr note "stop the server hosting this world first".
- `list`: the generator column becomes `stored:<key>:<version>` for stored worlds. The column count is unchanged.
- A new exit code, `ExitTerrainInvalid = 4`.
- The seed comes from the record, or from `GetOrCreateSimulationSeed` as the server does.

**Tests:**
- New `Server/Worlds/WorldExportCliTests.cs`:
  - export of a generated world with removals: the chunks omit removed cells, and the new world has no overrides and has its rubble;
  - a runtime built from it (C0 plumbing) has the same checksum as the restored source runtime;
  - export is deterministic (two exports, same bytes);
  - exporting a stored world works;
  - `--terrain-only` gives an empty world-scoped dump apart from chunks;
  - over-limit chunks give exit 4;
  - name conflict and usage errors.
- New `WorldResetTerrainTests.cs`:
  - overrides and rubble in restored cells are gone, while other rubble stays;
  - cells under a placed cube are kept and reported;
  - after a restart there is no heal (`terrain-override-healed` not logged) and the map equals the base;
  - a stored world resets to its own chunks;
  - a player or enemy saved inside a restored cell: record and report the behaviour, but do not fix it in C.
- Update `WaveBTesterCliTests` and `SqliteWorldCatalogTests` for the list format and the new members.

**Must not touch:** `Simulation/**`, the protocol, the client, `ServerBootstrap`.

### D: Safety net, soak, measurements and docs

**Owns** new tests only, plus docs.

- `Integration/StoredWorldJoinLoopbackTests.cs` (needs A and B, skip until glue):
  - over real TCP, a client joins a stored World10k; checksum, raised key sets and rubble are equal;
  - destroy terrain and a sculpture cell, restart, join again, equal again;
  - two clients join at once;
  - a generated world's join frame sequence and bytes equal the C0 capture.
- `Integration/StoredWorldEditsRestartTests.cs` (C0 only):
  - on a stored runtime: a terrain fracture writes 1 override plus N pieces;
  - after a restart: the cell is absent, rubble is restored, ids are unique, and a placed cube in a removed cell restores;
  - lockstep: two servers, terrain fractures, two restarts, with checksums and `DumpWorld` equal every tick.
- `Integration/StoredWorldSoakTests.cs`:
  - **Short (always):** a 30×8×30 stored world, 3 sessions of 3,000 ticks with a scripted fracture every 64 ticks (raised, sculpture and placed cubes, plus placements in removed cells), restarts between sessions. After each restart, check that override rows equal the removed source cells, there is no orphan rubble, ids are unique and below the counter, `integrity_check` is ok, and there are no invariant violations. The client-join leg is skipped until glue.
  - **Long (`DREAM_LOAD_TEST=1`):** World10k stored, 115,200 ticks (30 simulated minutes), a fracture every 128 ticks, a restart every 19,200 ticks (6 restarts), 2 clients joining after each restart. Same checks, plus the gates below.
- `Integration/TerrainResetLoopbackTests.cs` (needs C, skip until glue): reset, restart, a client joins and matches.
- `Load/StoredWorldMeasurementTests.cs`.
- **Docs:**
  - `docs/architecture.md`: stored hosting, the terrain transfer, the v8 order;
  - `docs/server-persistence-ddl.md`: no schema change, export and reset SQL semantics;
  - `docs/configuration.md`: the `--worlds` commands, exit codes, the offline rule;
  - `docs/ledger/work-log/2026-10-29-persistence-wave-e-baseline.md`, measured first, at C0.

**Must not touch:** any production code.

## Merge Order And Glue

Merge in the order C0, A, B, C, D, building and testing after each merge:
- A first: the wire is the shared dependency.
- B second: end-to-end works once A and B are both in.
- C and D last: they depend only on C0.

Glue:
- unskips D's tests;
- runs an end-to-end CLI flow on a copy of a real database: export, then host (`DREAM_WORLD=<exported>`), join, fracture, restart, reset-terrain, restart, join;
- checks that legacy databases still stay at v6 with no backup;
- runs the measurements back to back against C0;
- runs the perf harness on a stored world (two clients, FPS, draws, GL errors).

## Measurements And Gates

Release with `DREAM_LOAD_TEST=1`, two alternating back-to-back rounds, against C0.

| Measurement | Gate |
| --- | --- |
| World10k full checkpoint write, incremental write, 1,000-write burst | each ≤ C0 × 1.10 |
| Join tick mean, generated World10k | ≤ C0 × 1.10 |
| Generated World10k join transfer | byte-identical to C0 (232,677 B at wave D; C0 differs from wave D only in the version digit) |
| Stored World10k terrain frames, total bytes | ≤ 300,000 B (raw 215,996 B; base64 ≈ 287,996 B) |
| Stored World10k join transfer, total | ≤ C0 generated + 300,000 B |
| Terrain fragments, World10k | ≤ 6 |
| Join tick mean, stored World10k (server) | ≤ C0 generated × 1.10 (the payload is prebuilt; base64 is written off the tick) |
| Client stored map build (decode, Map, physics, render, non-headless) | ≤ 1.5 × client generated build, same process |
| Loopback join, carrier to applied world, client wall time | stored ≤ generated + 250 ms |
| Server start, stored World10k (load, payload, build) | ≤ 1.5 × generated |
| Payload resident bytes | ≤ 1.1 × raw blob bytes; no per-connection copy |
| Long soak | tick p99 ≤ C0 generated soak × 1.10; managed heap after full GC at restart k ≤ restart 1 × 1.10; invariants hold |
| Export World10k (bake and write) | report; ≤ 2 s |
| Reset-terrain with 628 overrides | report; ≤ 1 s |

## Risks

1. **base64 escaping.** The default JSON encoder escapes `+`. Use `WriteBase64String`; A pins that the output has no backslash.
2. **Hostile terrain.** A server or anything on the path can send blobs that decode to huge override sets. The decode budget, per-blob and total limits, and canonical order bound it. A's fuzz test is the proof.
3. **Client rebuild failure.** `ResetMapBodies` runs before the stored map is built. A decode failure leaves a broken map, but the session ends anyway as a protocol error. Documented.
4. **Reset or export against a running server.** A later checkpoint or the restore heal can silently undo a reset. Mitigations are the documented rule and the stderr note; a lease is Phase 5 (U10).
5. **`CopyPlacedCubesTo` on a source change** would replay the old map's removals. B must not call it for stored rebuilds; B's test pins this.
6. **Longer held-message window.** The terrain prelude adds about 5 frames before the world chunks. It stays within `MaxHeldMessages` and the deadline.
7. **Protocol v8.** Older clients get the existing mismatch message. In-process paths that bypass the codec must set `ReceivedTerrain` themselves.
8. **Stored worlds cannot be resized.** `RebuildMapForFloorSize` throws for them; its callers are test-only.
9. **Players or enemies saved inside cells that reset restores.** C records the behaviour. A fix, if one is needed, is a follow-up.
