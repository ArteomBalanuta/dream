Purpose: Record the wolf spawn-height fix so the enemy starts on top of the floor instead of inside the floor layer.
Status: Complete
Scope: Initial wolf spawn policy and focused spawn tests

# Summary

The map's floor cubes occupy cell `y = 0` with visual centers at `y = 0.5`, so the visible floor top is `y = 1.0`.

The wolf spawn policy previously returned `y = 0`, which placed the enemy anchor at the floor cell level. The spawn policy now exposes `FloorSurfaceY = 1f` and uses it for both default and clamped initial wolf positions.

# Verification

Commands run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~WolfSpawnPolicyTests" --nologo /p:UseSharedCompilation=false
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false
dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false
```

Observed results:

- focused wolf spawn-policy tests passed
- full test suite passed
- build passed with the known BulletSharp warning still present
