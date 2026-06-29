# Noclip Toggle Command Edge Buffering

Purpose: record the follow-up fix for movement becoming camera-disconnected after entering and exiting fly/noclip mode.
Status: completed
Scope: desktop command capture, local fixed-tick input buffering, remote command transport, focused movement/network tests

## Root Cause

The desktop command path transported jump, freeze, and noclip toggle as raw key-down booleans, while the movement motor consumes those values as one-shot press events.

That mismatch can desync local and server noclip state when render frames, the 64 Hz fixed tick, and network command drains do not line up exactly. A held `V` key can be observed as more than one server-side rising edge, or a short local press can be missed by the local fixed tick. Once client and server disagree about noclip state, the authoritative server resolves movement with a different movement mode than the local camera presentation, which feels like movement no longer respects the camera direction.

## Completed Work

- Added `PlayerCommandOneShotInputFilter` to convert raw key-down command frames into stable one-shot events for jump, freeze, and noclip toggle.
- Added a local `LatestPlayerCommandInbox` in `PlayScene` so one-frame press events survive until a fixed simulation tick consumes them.
- Updated `PlayScene` to send the same edge-filtered command stream to the remote server.
- Kept continuous movement axes, look deltas, requested mode, and primary-use state unchanged.
- Verified the local camera correction path still avoids applying authoritative facing to the client runtime.

## Tests

- Added `PlayerCommandOneShotInputFilterTests`.
- Added a `LatestPlayerCommandInbox` regression test proving a one-frame noclip toggle is preserved until drain.

## Verification

- focused:
  - `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~PlayerCommandOneShotInputFilterTests|FullyQualifiedName~LatestPlayerCommandInboxTests|FullyQualifiedName~ClientAuthoritativeStateApplierTests|FullyQualifiedName~ClientSimulationCommandPolicyTests|FullyQualifiedName~PlayerControllerTests|FullyQualifiedName~PlayerMotorTests|FullyQualifiedName~DedicatedServerHostTickTests|FullyQualifiedName~WorldSessionRuntimePresentationSmoothingTests"`
- full tests:
  - `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - result: `298` passing tests
- full build:
  - `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - result: succeeded with the existing `BulletSharp` compatibility warnings
