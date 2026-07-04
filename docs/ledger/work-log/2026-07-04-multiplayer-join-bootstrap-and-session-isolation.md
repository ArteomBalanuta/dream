Purpose: Record the multiplayer join visibility and player-session isolation fix.

Status: Completed

Scope: Server join bootstrap payloads, client remote-player presentation hydration, client correction metadata, and defensive client-side action-result session filtering.

## Findings

- Late joiners could receive roster and world data before they had any remote-player actor poses. The presenter renders remote players only from `ActorSnapshot` samples, so a join response without initial actors left visibility dependent on a later broadcast snapshot.
- Server-side base combat stats were already isolated in the direct two-player runtime path: updating the primary player's base stats did not mutate the secondary player's `RemoteSessionPlayerState`.
- Client correction still consumed the envelope-level interaction mode, which represents the primary controlled session. Secondary clients need their own actor-level `InteractionMode` to avoid host mode bleeding into local HUD/state.
- Client action-result application had no local-session guard. The server currently targets action results to the owning connection, but the client should still reject wrong-session stat/inventory results defensively.

## Changes

- Extended `JoinAcceptedMessage` with `InitialActors` so join bootstrap includes current player and enemy actor snapshots without waiting for the next tick broadcast.
- Added `WorldSessionRuntime.CaptureActorSnapshots()` so join bootstrap can reuse snapshot actor construction without draining pending world or damage deltas.
- Updated `ClientScenePresenter` and `ClientJoinAcceptedApplier` to hydrate remote-player presentation and local runtime correction from join-time actor snapshots.
- Updated client correction to prefer the local actor's replicated interaction mode over the snapshot envelope's primary-session mode.
- Added a local-session guard around client action-result application and matching inventory HUD/viewmodel application.

## Verification

- Targeted regression run passed:
  - `JoinSession_LateJoinerReceivesInitialActorSnapshotsForAlreadyJoinedPlayers`
  - `ProcessAction_PlayerStatsUpdateOnlyChangesActingPlayerStats`
  - `ApplyJoinAccepted_InitialActorsHydrateVisibleRemotePlayersForLateJoiner`
  - `TransportJsonCodec_RoundTrips_JoinAcceptedMessage_WithInitialActors`
  - `ClientAuthoritativeActionApplier_ApplyActionResult_IgnoresStatsForDifferentSession`
  - `ClientCorrectionApplier_ApplySnapshotToRuntime_UsesLocalActorInteractionMode`

