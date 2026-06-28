Purpose: Record runtime floating damage text tuning and default wolf respawn behavior.
Status: Completed
Scope: HUD settings, floating combat text presentation, crit depth, and scene enemy lifecycle.

# Floating Damage Tuning And Wolf Respawn

## Summary

The settings menu now exposes runtime sliders for floating damage text size, motion speed, and critical-hit depth. Floating combat text uses a tuning value object so default presentation and slider bounds are centralized instead of scattered as render constants.

Critical damage is now visually stronger than regular damage through a larger style multiplier, thicker outline, and a deeper camera-facing burst controlled by the crit-depth slider.

The scene also now guarantees a default wolf exists during gameplay. After inactive enemies are pruned, if the enemy registry has no active actors, `PlayScene` respawns the default wolf using the existing spawn policy.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "FloatingCombatTextTuningTests|FloatingCombatTextStylesTests|GameSettingsTests|HudInteractionRequestTests|EnemyRegistryTests" /p:UseSharedCompilation=false`
