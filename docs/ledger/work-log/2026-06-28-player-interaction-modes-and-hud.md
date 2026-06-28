Purpose: Record the split between build and combat primary-use routing and the upgrade of the existing gameplay HUD panel.
Status: Completed
Scope: Interaction mode input, single-route primary use, and top-left HUD status

# Summary

Added explicit `Building` and `Combat` gameplay interaction modes, moved left-click routing through one active-mode branch at a time, and replaced the placeholder top-left HUD score line with player HP and current mode.

# Verification

- `dotnet build dream\dream.csproj --nologo`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo`
