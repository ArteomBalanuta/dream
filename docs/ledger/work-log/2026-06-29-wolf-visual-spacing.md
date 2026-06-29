Purpose: Record the render-side wolf spacing fix that prevents the player camera from ending up inside the enemy mesh.
Status: Complete
Scope: Wolf visual presentation distance, render-safe placement helper, and focused regression tests

# Summary

The wolf's normalized size was already mathematically reasonable, but the model could still overlap the player camera because enemies do not yet have full collision against the player body.

Completed work:

- measured the final normalized wolf bounds and confirmed the oversized look was caused by camera overlap, not by raw scale
- added `EnemyVisualPlacement` as a pure render-side helper
- added `WolfAssetDefinition.MinimumVisualDistanceFromCamera`
- updated `EnemyRenderer` to resolve a render-safe wolf position before building the world model matrix
- added focused tests for render-safe visual placement and wolf visual-distance metadata

# Verification

Commands run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~EnemyVisualPlacementTests|FullyQualifiedName~WolfAssetDefinitionTests|FullyQualifiedName~EnemyActorTests" --nologo /p:UseSharedCompilation=false
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false
dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false
```

Observed results:

- focused visual-placement and enemy-spacing tests passed
- full test suite passed
- build passed with the known BulletSharp warning still present
