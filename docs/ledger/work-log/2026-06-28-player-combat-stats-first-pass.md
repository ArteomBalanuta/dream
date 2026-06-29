Purpose: Record the first player-owned combat stat slice and its HUD/runtime menu integration.
Status: Completed
Scope: Player combat stats, cooldown-based attack cadence, crit feedback, HUD range display, and runtime stat editing

# Summary

Replaced the ad hoc combat damage roll with player-owned `Strength`, `Attack Speed`, and `Vitality` stats. Combat mode now respects cooldown-based attack cadence, critical hits publish a dedicated red floating-text style, and the top-left HUD plus settings menu expose the current combat tuning live at runtime.

# Verification

- `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false /p:BaseOutputPath=G:\\workspace\\projects\\dream\\artifacts\\tests\\ /p:BaseIntermediateOutputPath=G:\\workspace\\projects\\dream\\artifacts\\obj-tests\\`
