# Client Facing Command Intent for Authoritative Movement

Purpose: Record the fix for movement no longer matching the player's visible camera direction after changing mouse sensitivity.

Status: Completed on 2026-07-01.

Scope: Client/server command protocol, remote authoritative player simulation, and command coalescing.

## Problem

Remote-authoritative movement reconstructed player facing on the server by replaying raw mouse deltas with server-side mouse sensitivity. After the client changed mouse sensitivity locally, the client camera and server-side reconstructed facing could diverge. Forward/strafe movement was then authoritative against the server's stale/different facing, so WASD felt like it no longer respected the camera the player saw.

## Change

- Extended `PlayerLookIntent` with an optional `Facing` vector.
- Attached the local camera's current forward vector to remote command frames after immediate local look has been applied.
- Added `RemotePlayerCommandRelay` so remote commands intentionally carry facing direction while zeroing raw look deltas.
- Updated the primary authoritative player path to apply command-facing before resolving movement, so `W` follows the client-visible camera direction.
- Expanded regression coverage for primary authoritative forward movement, strafe/right-axis movement, snapshot-facing publication, and legacy commands without a facing vector.
- Updated remote authoritative player simulation to prefer the command-facing vector for actor facing and movement direction, falling back to raw delta replay for older commands without facing.
- Preserved the latest command-facing vector when coalescing pending command frames.
- Added protocol and remote-player regression coverage for command-facing preservation and movement direction.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~ApplyCommand_UsesClientFacingForMovementDirection|FullyQualifiedName~TransportJsonCodec_RoundTrips_PlayerCommandMessage_WithClientFacing"`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~RemoteSessionPlayerStateTests|FullyQualifiedName~ProtocolFoundationTests|FullyQualifiedName~LatestPlayerCommandInboxTests|FullyQualifiedName~PlayerControllerTests|FullyQualifiedName~ClientSimulationCommandPolicyTests|FullyQualifiedName~GameplayInputResumeFilterTests"`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~RemotePlayerCommandRelayTests|FullyQualifiedName~TransportJsonCodec_RoundTrips_PlayerCommandMessage_WithClientFacing|FullyQualifiedName~RemoteSessionPlayerStateTests|FullyQualifiedName~LatestPlayerCommandInboxTests|FullyQualifiedName~ClientAuthoritativeStateApplierTests"`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~AdvanceTick_PrimaryRemoteCommandMovesForwardAlongClientFacing"`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~RuntimeAuthoritativeSessionHostTests|FullyQualifiedName~RemotePlayerCommandRelayTests|FullyQualifiedName~RemoteSessionPlayerStateTests|FullyQualifiedName~PlayerControllerTests|FullyQualifiedName~ProtocolFoundationTests|FullyQualifiedName~ClientAuthoritativeStateApplierTests|FullyQualifiedName~ClientSimulationCommandPolicyTests"`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~RuntimeAuthoritativeSessionHostTests"`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
