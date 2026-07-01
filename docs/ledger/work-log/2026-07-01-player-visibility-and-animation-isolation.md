Purpose: record fixes for second-client host visibility and shared player animation playback
Status: implemented
Scope: client presenter local-session identity, player model animation ownership, focused regression tests

## Root Causes

Second-client host visibility depended on the `ClientScenePresenter` constructor session id staying identical to the server-accepted session id. That is fragile because the presenter is created before `server.join-accepted` is applied. If those ids diverge, the presenter can filter the wrong actor as local or keep the real local actor in the remote presentation set.

Player animation playback was also cached too broadly. `AnimatedModel` owns mutable playback and glTF node transform state, so sharing one cached player model across all local/remote player draws lets one actor's `walk` clip affect every rendered player.

## Changes

- `ClientScenePresenter.ApplyJoinAccepted(...)` now treats the accepted session id as the authoritative local session filter.
- Added `PlayerAnimationClipTracker` to track active clip names per rendered actor id.
- Added `ModelAssets.CreatePlayerInstance()` for uncached player model instances.
- `LocalPlayerRenderer` now owns a lazy local player model instance instead of using the global cached player model.
- `RemotePlayerRenderer` now owns one lazy player model instance per remote actor id and prunes instances when actors disappear.

## Verification

- Added regression coverage for accepted-session local filtering so a later client sees the first joined host/player as remote.
- Added clip-tracker coverage so one actor repeating or changing animation does not alter another actor's active clip state.
- Focused tests passed for `ClientScenePresenterTests`, `PlayerAnimationClipTrackerTests`, and `RemotePlayerRendererTests`.
