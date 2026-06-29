Purpose: record the fix for client/server floor mismatch during remote multiplayer startup.
Status: completed
Scope: join-accepted initial world hydration, startup floor-size synchronization, regression coverage, and verification notes

# Summary

Remote clients could begin gameplay with a floor that did not match the authoritative server map. The most visible symptom was that some floor surface looked present locally while the authoritative solid area was smaller, which let the player walk or fall into non-authoritative space and then fall through.

# Root Cause

- The authoritative runtime default in `RuntimeAuthoritativeSessionHost.CreateDefault()` currently creates a `10 x 10` world.
- The desktop client runtime in `PlayScene` still bootstraps locally from `GameSettings.FloorSize`, which defaults to `25`.
- `JoinAcceptedMessage` already carried `InitialWorld`, but the client did not apply that payload to the runtime.
- Regular `server.snapshot` broadcasts currently omit the full `WorldSnapshot` by default, so the missed join-time hydration could leave the client on the wrong base floor indefinitely.

# Changes

- Added `ClientJoinAcceptedApplier` so join acceptance can hydrate the local runtime from the authoritative initial world payload.
- Updated `PlayScene.ApplyReplicatedMessage(...)` so remote clients apply `JoinAcceptedMessage.InitialWorld` as soon as the message is received.
- Added a regression test proving that join acceptance rebuilds the client floor to the authoritative size before normal remote snapshot flow continues.

# Verification

- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`

# Notes

- The build and tests still report the known `NU1701` warning for `BulletSharp`.
- This fix closes the startup floor mismatch hole. If authoritative floor-size changes later become live server actions, the runtime will still need an explicit replicated floor-resize event or periodic full-world snapshot path for mid-session map-resize sync.
