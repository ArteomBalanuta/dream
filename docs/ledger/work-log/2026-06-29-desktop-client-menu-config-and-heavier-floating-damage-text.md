# Desktop Client Menu Config And Heavier Floating Damage Text

Purpose: record the client-config persistence pass for startup/menu values and the floating combat text presentation retune.
Status: completed
Scope: desktop startup config, client-local settings persistence, floating damage text defaults and lifetime behavior

## Summary

- added a new text-backed desktop client config at `dream/config/desktop-client.txt`
- moved startup connect-form defaults (`Host`, `Port`, `Player Name`) into that config when environment overrides are not present
- moved client-local settings-menu defaults into the same config:
  - `Field Of View`
  - `Mouse Sensitivity`
  - `Fly Speed`
  - `Strength`
  - `Attack Speed`
  - `Vitality`
  - floating damage text tuning
- wired startup/menu edits to persist back into the config automatically
- retuned floating damage text defaults so hits render larger, linger longer, and critical hits push farther toward the camera
- fixed the floating-text lifetime seam so slower motion-speed tuning extends real on-screen lifetime instead of still hard-cutting at the old one-second limit

## Implementation Notes

- `DesktopClientConfig` now acts as the small shared data contract for desktop client defaults.
- `DesktopClientTextConfig` follows the existing repo text-config pattern used by the viewmodel rig config.
- `Game` now loads the desktop client config once, seeds both `StartupConnectionScene` and `GameSettings`, and persists updates through shared save callbacks.
- `PlayScene` now seeds initial player combat stats from that config and writes client-local settings changes back into the same file.
- `CombatFeedbackSystem` and `FloatingCombatTextSystem` now accept tuning-aware updates so slower damage-text speed affects lifetime consistently.

## Verification

- focused tests:
  - `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~DesktopClientTextConfigTests|FullyQualifiedName~FloatingCombatTextTuningTests|FullyQualifiedName~GameSettingsTests|FullyQualifiedName~CombatFeedbackSystemTests"`
- full tests:
  - `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - result: `294` passing tests
- full build:
  - `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - result: succeeded with the existing `BulletSharp` compatibility warnings
