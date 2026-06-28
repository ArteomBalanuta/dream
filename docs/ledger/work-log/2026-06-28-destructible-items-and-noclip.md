Purpose: Record the first destructible-items gameplay slice and the addition of debug noclip movement.
Status: Completed
Scope: Placed-cube combat registration, destruction lifecycle, combat hit resolution, and noclip/fly controls

# Summary

Added a destructible-cube registry that maps placed cubes into the existing combat capability model without putting permanent HP state onto every `Cube`. Combat mode now resolves real cube hits, damages registered placed cubes, and removes them cleanly on lethal damage while preserving the base floor as non-destructible in this first pass.

Also added a debug noclip/fly mode on `V`, with camera-relative movement, upward movement on `Space`, downward movement on `Left Ctrl`, and a movement-resolution seam that keeps the input logic testable without native Bullet dependencies.

# Verification

- `dotnet build dream\dream.csproj --nologo`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo`
