Purpose: Record the first world-mutation event slice on top of the scene-local event architecture.
Status: Completed
Scope: cube placement and destruction event payloads, scene publish points, and trace subscriber coverage

# Summary

Added `CubePlacedEvent` and `CubeDestroyedEvent` as the first world-mutation facts published by `PlayScene` after real map mutations succeed.

# Completed Work

- added `CubePlacedEvent` and `CubeDestroyedEvent` payloads
- published placement events after successful build-mode cube placement
- published destruction events after successful combat-driven cube removal
- extended `EventTraceSystem` so debug tracing can observe world-mutation events alongside combat events
- updated architecture notes to reflect the broader scene-local event slice

# Verification

- `dotnet build dream\\dream.csproj --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\`
