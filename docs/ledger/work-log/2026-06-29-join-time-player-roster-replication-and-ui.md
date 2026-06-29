# Join Time Player Roster Replication And UI

Purpose: record the multiplayer identity follow-up that closes the remaining join-time roster gap from the earlier player-registry and display-name ledger notes.
Status: complete
Scope: roster DTOs, join-accepted protocol payloads, client roster hydration, presenter-owned roster cache, and multiplayer roster UI

## Completed

- Added `PlayerRosterEntry` as the first explicit player-roster DTO carrying:
  - `SessionId`
  - `ActorId`
  - `DisplayName`
- Extended `JoinAcceptedMessage` so a successful join can now include the current joined-player roster instead of only world/bootstrap data.
- Updated `RuntimeAuthoritativeSessionHost` to include the current non-local joined player roster in every `JoinAcceptedMessage`.
- Added a client-owned roster cache to `ClientScenePresenter`.
- Added join-time roster hydration through `ClientJoinAcceptedApplier.ApplyToPresenter(...)`.
- Kept that client roster current from later authoritative player snapshots, so the cache does not depend only on the original join message forever.
- Updated the gameplay HUD/settings UI to show:
  - current player count in the HUD
  - a `Connected Players` roster section in the settings window

## Why

The previous identity work stored display names authoritatively and rendered them above actors, but the ledger correctly noted that the client still had no first-class roster model.

This slice closes that gap without overreaching into roster delta messages yet:

- join time now hydrates a real client roster
- later snapshots keep that roster fresh
- multiplayer UI can consume roster state through `ClientScenePresenter` instead of reading transport messages directly

## Verification

- Focused roster-contract verification:
  - `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~ProtocolFoundationTests|FullyQualifiedName~RuntimeAuthoritativeSessionHostTests|FullyQualifiedName~ClientScenePresenterTests|FullyQualifiedName~ClientAuthoritativeStateApplierTests|FullyQualifiedName~ProtocolWiringTests"`
- Full build and full test verification were run after implementation.
