# Connected Endpoint HUD Title And Session Display Names

Purpose: record the multiplayer UX follow-up that surfaces the connected server endpoint in the client UI and strengthens the server-side player session registry with display names.
Status: complete
Scope: gameplay HUD/settings visibility, window-title connection context, session registry identity, and follow-up planning for replicated player rosters

## Completed

- Added a small `GameWindowTitleBuilder` seam so the main window title now includes:
  - the base product name
  - the connected server `IP:port` when a remote session is active
  - the current FPS
- Added a reusable `RemoteClientOptions.EndpointLabel` so the connected endpoint is formatted in one place.
- Updated the gameplay HUD and settings window to show the currently connected server endpoint.
- Extended `ConnectedPlayer` and `PlayerSessionRegistry` so the authoritative registry now retains a normalized display name per session.
- Updated remote join handling so requested player names are stored in the authoritative registry instead of being discarded after join.
- Added focused tests for:
  - window title formatting
  - display-name fallback behavior
  - join-time display-name persistence on the authoritative host

## Why

This makes the multiplayer client more trustworthy during local testing because the player can now see exactly which server endpoint is active both in-window and in the title bar.

It also hardens the identity seam without pretending the client already has full replicated roster/name support.

## Remaining Gap

Player names are now retained authoritatively on the server, but they are not yet replicated to all clients as a first-class roster/presentation model.

That follow-up is tracked in:

- `docs/ledger/analysis/2026-06-29-multi-client-shared-world-and-player-identity-audit.md`
- `docs/ledger/plans/2026-06-29-player-registry-and-name-replication.md`
