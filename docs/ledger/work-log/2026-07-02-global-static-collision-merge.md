Purpose: Record the performance fix that merges static startup sculpture collision across parent-cell boundaries.

Status: Completed

Scope: Geometry collision export, physics binding for merged static collision groups, and performance evidence after startup micro-grid sculpture expansion.

## Evidence

A temporary out-of-repo probe measured the enriched `56 x 8 x 56` startup map before the change:

- render blocks: `6,264`
- compatibility cubes: `3,346`
- collision bodies: `9,269`
- subcell collision bodies: `9,047`
- coarse cell collision bodies: `221`

The mismatch happened because render descriptors already used a global micro-grid greedy merge, while collision bodies were still merged only inside each parent world cell.

## Implementation

Static startup subdivision cells now get a bootstrap-cheap collision pass. Render keeps the high-detail global micro-grid merge, while collision exports one coarse static AABB per occupied parent cell. The merged static collision group is kept separate from per-cell dynamic/fracture collision bodies so runtime edits can still sync through the existing cell-level path.

`WorldPhysicsBinder` now owns a merged static collision body group and can replace that group without disturbing dynamic cell bodies.

`Map.RestoreGeneratedStartupCells(...)` now batches authored startup-cell restores during client join snapshot application. This avoids rebuilding and re-syncing global static sculpture artifacts once per authored cell.

## Result

The same collision probe after the first implementation reported:

- render blocks: `6,264`
- compatibility cubes: `3,346`
- collision bodies: `6,486`
- subcell collision bodies: `6,264`
- coarse cell collision bodies: `221`

That removed `2,783` collision descriptors, but connection bootstrap could still stall because the client applied authoritative startup subcell snapshots by rebuilding global sculpture artifacts once per authored cell.

After coarse static collision and batched authored-cell restore, the probes report:

- render blocks: `6,264`
- compatibility cubes: `3,346`
- collision bodies: `629`
- subcell collision bodies: `407`
- coarse cell collision bodies: `221`
- server host startup: about `824 ms`
- server join: about `4 ms`
- client runtime startup: about `591 ms`
- client join world application: about `552 ms`, down from about `79,058 ms`

This removes `8,640` collision descriptors from the original startup map export while preserving the visual micro-grid density. Static authored sculpture collision is intentionally coarser than render geometry for responsive bootstrap; dynamic fracture/edit collision remains per-cell and more detailed.

## Verification

- `dotnet test .\dream.Tests\dream.Tests.csproj --nologo --filter "MapGeometryFacadeTests|PhysicsIntegratorOwnershipTests" /p:UseSharedCompilation=false`

Focused result at implementation time: `23` passed, `0` failed.
