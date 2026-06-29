Purpose: Record the first refactor that broadens destructible world-object lifecycle beyond cube-specific registry types.
Status: Completed
Scope: Generic world-object registry/runtime record seam with cube adapters and scene integration

# Summary

Replaced the cube-specific destructible registry shape with a generic world-object registry and record model, while keeping cubes as the first concrete adapter.

# Completed Work

- introduced `WorldObjectRegistry` and `WorldObjectRecord`
- moved cube registration onto `RegisterCube(...)` as a specific adapter path over the generic registry
- switched combat hit resolution and fatal removal flow to use generic world-object records
- preserved cube snapshot and floor-rebuild behavior through cube-specific snapshot helpers on the generic registry
- added tests proving the registry can now host a non-cube object with a custom lifecycle callback

# Verification

- `dotnet build dream\\dream.csproj --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`
