# 2026-07-04 Protocol Parity First Slice

Purpose: record the first implementation step toward making every desktop player use the same client/server protocol.
Status: complete
Scope: desktop gameplay scene construction, no-remote authority branch removal, secondary command-driven primary-use regression, and documentation alignment.

## Summary

Normal desktop gameplay now requires a `RemoteClientSession`. `Game` was already constructing `PlayScene` only after the startup connection flow returned a connected session, but `PlayScene` still accepted `null` and kept no-remote local-authority branches alive.

This pass makes the type shape match the desired architecture:

- `PlayScene` requires a protocol client session.
- connected snapshots, join messages, and action results always use the remote-authority application path.
- local desktop ticks no longer enable gameplay simulation inside the client-side mirror runtime.
- stats and inventory HUD actions always send protocol `PlayerActionMessage` requests.
- the secondary-session command-driven primary-use bridge remains covered by combat and building regression tests.

## What Still Remains

- Integrated local play still uses TCP loopback; in-memory loopback transport is the next transport optimization.
- The client-side `WorldSessionRuntime` still exists as a presentation mirror and correction target.
- Floor-size and other gameplay-affecting settings still need explicit server-owned protocol actions or connected-mode disabling.
- Server-side actor depth is still asymmetric until the full multi-client actor-equivalence work lands.
- The integrated host still has a temporary command primary-use synthesis bridge for secondary sessions; a later actor-equivalence pass should unify primary and secondary command intake even further.

## Verification

- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~RuntimeAuthoritativeSessionHostTests|FullyQualifiedName~WorldSessionRuntimeCombatEventQueueTests|FullyQualifiedName~AuthoritativeActionFlowTests|FullyQualifiedName~DedicatedServerHostTickTests|FullyQualifiedName~NetworkSessionCoordinatorTests|FullyQualifiedName~RemotePlayerCommandRelayTests|FullyQualifiedName~ClientSimulationCommandPolicyTests"`
- `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`
