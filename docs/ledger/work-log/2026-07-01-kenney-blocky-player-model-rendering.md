# Kenney Blocky Player Model Rendering

Purpose: Record the first player-model rendering pass that replaces remote-player cube markers with an animated blocky character asset.

Status: Completed on 2026-07-01.

Scope: Remote-player presentation, model asset import, animation clip selection, and render-state transform seams.

## Asset

- Source: Kenney Blocky Characters 2.0.
- License: Creative Commons Zero (CC0), copied to `dream/animations/players/LICENSE-Kenney-Blocky-Characters.txt`.
- Imported files:
  - `dream/animations/players/kenney-blocky-character-a.glb`
  - `dream/animations/players/Textures/texture-a.png`

## Change

- Added `PlayerModelAssetDefinition` for player model path, authored forward axis, clip names, and head-node name.
- Added `PlayerRenderState` to build world-space player model matrices from replicated pose/facing and choose `idle` versus `walk`.
- Replaced `RemotePlayerRenderer` cube markers with the shared animated-model renderer path.
- Added optional per-node overrides to `AnimatedModel.RenderPrepared(...)` so the player `head` node has a clean future hook for independent head/aim rotation.
- Extended `RemotePlayerPresentationState` with `IsMoving` and set it in `ClientScenePresenter` from authoritative snapshot position deltas.

## Notes

The current protocol carries one look direction. The body model rotates to that direction, while the head override is currently identity to avoid double rotation. A future protocol slice can split body-facing and aim/head-facing if we want visible head pitch or independent head yaw.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~PlayerModelAssetDefinitionTests|FullyQualifiedName~PlayerRenderStateTests|FullyQualifiedName~ClientScenePresenterTests|FullyQualifiedName~RemotePlayerRendererTests"`
