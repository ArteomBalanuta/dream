Purpose: record fixes for floating player models and missing host-model visibility on later clients
Status: implemented
Scope: player render transform, authoritative primary remote-session ownership, focused regression tests

## Root Causes

The blocky player model was translated to the player body's center position. The player body center is currently `Y=1.5`, while its foot plane sits at `Y=1.0` on top of the floor. Rendering the model at body-center height made the imported character appear to hover above the ground.

The multiplayer runtime also resolved the primary controlled remote session by scanning the session registry for the first non-local player. That made primary actor ownership implicit and fragile. Later clients need the first joined remote session to remain the stable owner of the primary actor so their presenter can treat that actor as a remote host/player model instead of losing it through self-filtering or ownership ambiguity.

## Changes

- Added `PlayerModelAssetDefinition.BodyCenterToFootOffset`.
- Applied that offset in `PlayerRenderState` for both local third-person and remote player models.
- Added `WorldSessionRuntime.PrimaryRemoteSessionId`.
- Set `PrimaryRemoteSessionId` when the first remote session attaches to the full player actor.
- Updated `WorldSessionRuntime` and `RuntimeAuthoritativeSessionHost` to use the explicit primary session instead of registry enumeration.
- Added regression coverage for grounded render transforms and second-client visibility of the first joined primary actor.

## Verification

Focused verification covered:

- `PlayerRenderStateTests`
- `RuntimeAuthoritativeSessionHostTests`
- `WorldSessionRuntimeMultiplayerEnemyTests`
- `ClientScenePresenterTests`

