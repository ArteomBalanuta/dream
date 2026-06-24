# First-Person Viewmodels And Skins

**Purpose:** Record the completed first milestone for first-person hands and held-item presentation.

**Status:** Completed

**Scope:** Renderer extraction, runtime viewmodel state, named clip playback support, blockout assets, and the documentation pass for the new first-person foundation.

## Summary

This milestone followed a renderer-first implementation strategy. The first change was extracting `ViewModelRenderer` as a dedicated collaborator owned by `Render`, then threading a separate first-person update and draw pass through `PlayScene` before landing visible hands or held-item behavior.

That sequence kept `Render.cs` as the shared low-level renderer host while moving first-person rules into focused collaborators. `PlayerViewModelController` now owns runtime presentation state, `ViewModelCatalog` plus `ViewModelAssets` define the built-in hands and held-item loadout, and `AnimatedModel` now supports named `Idle` and `Use` clip playback with loop and one-shot semantics.

## Files Added

- `dream/ViewModelRenderer.cs`
- `dream/ViewModelSlot.cs`
- `dream/ViewModelDefinition.cs`
- `dream/ViewModelLoadout.cs`
- `dream/ViewModelCatalog.cs`
- `dream/ViewModelAnimationRequest.cs`
- `dream/ViewModelSlotState.cs`
- `dream/ViewModelFrameState.cs`
- `dream/PlayerViewModelController.cs`
- `dream/AnimationPlaybackMode.cs`
- `dream/AnimationPlaybackState.cs`
- `dream/ViewModelAssets.cs`
- `dream/animations/viewmodels/DebugHands.gltf`
- `dream/animations/viewmodels/DebugHands.bin`
- `dream/animations/viewmodels/DebugHeldItem.gltf`
- `dream/animations/viewmodels/DebugHeldItem.bin`
- `dream.Tests/ViewModelCatalogTests.cs`
- `dream.Tests/PlayerViewModelControllerTests.cs`
- `dream.Tests/AnimationPlaybackStateTests.cs`

## Files Modified

- `dream/Render.cs`
- `dream/PlayScene.cs`
- `dream/Player.cs`
- `dream/AnimatedModel.cs`
- `dream/ModelAssets.cs`
- `docs/architecture.md`
- `docs/rendering-and-assets.md`
- `docs/build-and-run.md`

## Verification

Commands run during implementation:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo
dotnet build dream\dream.csproj --nologo
dotnet run --project dream\dream.csproj
```

Observed verification state:

- `dotnet test dream.Tests\dream.Tests.csproj --nologo` passed with `59/59` tests after the final Task 5 work.
- `dotnet build dream\dream.csproj --nologo` passed during the implementation sequence with the repo's expected `BulletSharp` `NU1701` warnings still present.
- `dotnet run --project dream\dream.csproj` was used for manual runtime verification of first-person presentation.

## Runtime Observations

- The first-person pass rendered after world and collision-debug drawing, but before HUD and ImGui.
- `ViewModelRenderer` used the live viewport aspect and cleared only depth before drawing, so world geometry stayed behind the hands and held item without overwriting the color buffer.
- The blockout hands and held item both rendered as independent first-person slots.
- Left click triggered the `Use` one-shot and both models returned to looping `Idle` playback after the action completed.
- Manual verification screenshots were kept locally under `artifacts/` and were not committed.

## Follow-Up Notes

- Replace the blockout assets in `dream/animations/viewmodels/` with real art once the animation and proportions are approved.
- Add player-facing skin-selection UI later on top of the existing `ViewModelCatalog` and `ViewModelLoadout` foundation instead of hardcoding more swaps into scene code.

## Viewmodel Tuning Rig

- Added shared viewmodel FOV and transform settings in `GameSettings.ViewModelRig`.
- Added `ViewModelPoseResolver` so final first-person poses are built outside `Render.cs`.
- Added body-anchor illusion controls that bias the hands and held item downward and outward so they feel more torso-connected.
- Added per-slot `hands` and `held item` tuning controls in the runtime settings menu.
