# Noclip Exit Grounded Movement Direction Fix

Purpose: record the regression fix for grounded movement losing camera-relative horizontal intent immediately after exiting noclip/flying mode.
Status: complete
Scope: root-cause note, grounded movement sanitization, regression coverage, and verification

## Root Cause

On the exact frame that noclip was toggled off, the grounded motor path could still receive a `MovementCommand.WorldDirection` that had been resolved using noclip semantics.

That meant the direction could contain a large vertical component from the flying camera pitch.

The grounded motor then used only the `X/Z` parts of that already-normalized 3D vector for horizontal speed, which could collapse movement almost to zero and make forward input feel disconnected from the camera after flying.

## Completed

- Updated `PlayerMotor` to reproject grounded movement direction onto the world plane before applying move speed.
- Added a focused regression test that reproduces noclip exit with a steep vertical-leaning move vector and verifies that grounded horizontal speed is preserved.

## Verification

- Focused regression verification:
  - `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~PlayerMotorTests"`
  - `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~PlayerMotorTests|FullyQualifiedName~PlayerControllerTests|FullyQualifiedName~PlayerMovementResolverTests|FullyQualifiedName~WorldSessionRuntimePresentationSmoothingTests"`
- Full build and full test verification were run after the fix.
