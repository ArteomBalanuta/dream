Purpose: Record the completed first-pass server-backed HUD inventory slice and the matching persistence documentation updates.
Status: Completed
Scope: DDL documentation, authoritative starter inventory/equip flow, HUD overlay UX, held-tool viewmodel switching, and regression coverage.

## Completed

- documented the live SQLite schema in [docs/server-persistence-ddl.md](/G:/workspace/projects/dream/docs/server-persistence-ddl.md)
- added the approved inventory/tool spec and execution plan
- introduced shared inventory/tool contracts under `dream/Simulation/Inventory/`
- added authoritative inventory equip protocol payload/result types
- extended `JoinAcceptedMessage` so the client receives its initial inventory snapshot at join
- loaded and restored persisted item instances plus slot assignments in `WorldSessionRuntime`
- seeded starter inventory for first-time players and persisted the equipped slot
- implemented authoritative equip validation and authoritative snapshot return on equip
- added a standalone HUD inventory overlay toggled with `V`
- remapped noclip/fly toggle from `V` to `N`
- added hover tooltip details and click-to-equip behavior
- switched held-item viewmodels from authoritative `visual_id` data

## Verification

- `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`

## Notes

- The first per-tool visual seam is now in place, but the current held-item variants still reuse the existing held-item asset.
- Remote third-person held-item attachments remain future work; this slice covers server-owned inventory state plus first-person HUD/viewmodel behavior.
