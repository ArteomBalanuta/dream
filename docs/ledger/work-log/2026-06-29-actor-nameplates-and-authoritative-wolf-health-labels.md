# Actor Nameplates And Authoritative Wolf Health Labels

Purpose: record the multiplayer presentation update that replicates actor display names and renders always-on world labels for remote players and enemies.
Status: complete
Scope: snapshot display-name metadata, remote player presentation state, enemy label identity, wolf health normalization, world-label rendering, and verification

## Completed

- Extended `ActorSnapshot` with optional replicated `DisplayName` metadata.
- Updated authoritative snapshot export so player snapshots now carry the server-side session display name from `PlayerSessionRegistry`.
- Updated authoritative enemy snapshots so the current wolf exports as `Wolf`.
- Added `DisplayName` support to `EnemyActor` and remote enemy snapshot application so replicated enemies preserve their authoritative label identity on clients.
- Added `DisplayName` support to `ClientScenePresenter` and `RemotePlayerPresentationState` so remote-player presentation keeps the replicated player name instead of falling back to session id only.
- Added `ActorWorldLabelBuilder` and `WorldActorLabelState` as a clean presentation seam for always-on world labels above replicated actors.
- Updated the HUD render path to draw always-on world labels for:
  - remote players
  - active enemies
- Standardized the authoritative default wolf to `100` max HP so the visible `Wolf 100/100` baseline matches the real gameplay state.

## Result

Connected clients can now join with a player name, see that name above remote player actors, and see the wolf labeled in-world with authoritative live HP such as `87/100`.

Because both the name and the health data come from authoritative state, all connected clients converge on the same visible labels instead of inventing local-only text.

## Verification

- Focused red-phase verification:
  - `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~WorldSessionRuntimeSnapshotDisplayNameTests|FullyQualifiedName~ActorWorldLabelBuilderTests|FullyQualifiedName~ClientScenePresenterTests|FullyQualifiedName~RemoteEnemyStateApplierTests|FullyQualifiedName~ProtocolFoundationTests|FullyQualifiedName~RemotePlayerRendererTests"`
- The focused suite then passed after implementation with `17` passing tests.
- Full build and full test verification were run after the implementation pass.
