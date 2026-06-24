# Performance First-Pass Implementation Work Log

**Purpose:** Record the first implementation pass for reducing the large-floor performance costs in `dream`.

**Status:** Complete

**Scope:** World storage, floor physics representation, render-loop iteration, shader hot-path cleanup, and verification

## Summary

Implemented the first bounded performance pass without jumping to chunking or a renderer rewrite. The world now separates width, height, and depth bounds, avoids eager `Cube` allocation for empty cells, renders from a direct solid-cube list, uses one merged static collider for the base floor instead of one rigid body per tile, syncs only dynamic map bodies during updates, and caches shader uniform locations for repeated render-path uniform use.

## Files Changed

- `dream/Map.cs`
- `dream/PhysicsIntegrator.cs`
- `dream/Game.cs`
- `dream/Player.cs`
- `dream/Shader.cs`
- `dream.Tests/MapPerformanceStructureTests.cs`
- `README.md`
- `docs/architecture.md`
- `docs/project-structure.md`
- `docs/rendering-and-assets.md`

## Verification

### Commands run

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter MapPerformanceStructureTests --nologo
dotnet build dream\dream.csproj --nologo
dotnet test dream.Tests\dream.Tests.csproj --nologo
$exe = Resolve-Path 'dream\bin\Debug\net8.0\dream.exe'
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
Start-Sleep -Seconds 5
$p.Refresh()
$alive = -not $p.HasExited
$title = $p.MainWindowTitle
$handle = $p.MainWindowHandle
$closed = $p.CloseMainWindow()
Start-Sleep -Seconds 2
$p.Refresh()
if (-not $p.HasExited) { Stop-Process -Id $p.Id }
```

### Observed results

- `dotnet test dream.Tests\dream.Tests.csproj --filter MapPerformanceStructureTests --nologo` passed with `2` tests
- `dotnet build dream\dream.csproj --nologo` succeeded with existing package warnings
- `dotnet test dream.Tests\dream.Tests.csproj --nologo` passed with `15` tests
- the built application started successfully, stayed alive for 5 seconds, exposed a native window handle, updated its window title to an FPS value, and closed cleanly

## Notes

- This pass removed obvious structural waste, but it did not yet produce a direct before/after FPS benchmark at a larger floor size such as `100 x 100`
- The current code should scale materially better than before because floor width/depth no longer imply cubic eager allocation and because the base floor no longer creates one static rigid body per tile
- Larger rendering optimizations such as chunking, merged meshes, instancing, or culling remain separate follow-up work if needed
