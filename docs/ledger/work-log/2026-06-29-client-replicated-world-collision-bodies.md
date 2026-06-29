# Client Replicated World Collision Bodies

Purpose: record the remote-multiplayer collision fix that makes replicated world cubes create local client physics bodies instead of remaining render-only.
Scope: replicated world snapshot/mutation physics application, client-side collision parity, focused regression coverage, and verification notes

## Summary

The local client could fall through replicated blocks because remote world application created those cubes with `withPhysics: false`.

That meant the client map could render cubes that had no matching collision bodies in the local Bullet world.

## Completed Work

- Updated `RemoteWorldMutationApplier` so replicated placed cubes now use normal `Map.TryAddCube(...)` behavior instead of forcing physics off.
- This applies to both:
  - full world snapshot reconciliation
  - incremental world mutation intake
- Added a focused runtime test proving that applying a replicated world snapshot to a `WorldSessionRuntime` increases local collision bodies for placed cubes.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "ClientAuthoritativeStateApplierTests" /p:UseSharedCompilation=false`

## Notes

- This keeps the client responsible for its own local collision model against the replicated world, which matches the desired multiplayer boundary better than relying on later net correction to paper over missing local physics.
