# Settings Menu Resume Look Delta Filter

Purpose: Record the fix for camera/WASD coordination breaking after changing mouse sensitivity in the settings menu.

Status: Completed on 2026-06-29.

Scope: Local gameplay input capture and immediate camera look handling.

## Problem

Changing mouse sensitivity through the ImGui settings menu could make the next gameplay frame feel like WASD no longer respected the camera. The movement pipeline still used the camera-facing vector, but the first gameplay frame after closing the menu could receive leftover UI/cursor mouse delta, amplified by the newly changed sensitivity.

## Change

- Added `GameplayInputResumeFilter` to suppress only the first look delta after gameplay input resumes.
- Preserved movement, mode-switch, and primary-use inputs on the resume frame.
- Wired the filter before one-shot command filtering in `PlayScene`.
- Added regression coverage for the blocked-to-active gameplay input transition.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~GameplayInputResumeFilterTests"`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~GameplayInputResumeFilterTests|FullyQualifiedName~PlayerCommandOneShotInputFilterTests|FullyQualifiedName~PlayerControllerTests|FullyQualifiedName~CameraTests|FullyQualifiedName~ClientSimulationCommandPolicyTests"`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
