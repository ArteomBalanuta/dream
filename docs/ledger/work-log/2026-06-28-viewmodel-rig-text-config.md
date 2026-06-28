# Viewmodel Rig Text Config

Date: 2026-06-28
Status: Completed
Owner: Codex

## Summary

Replaced the code-only startup ownership of the held-item and body-anchor defaults with a dedicated text-backed config flow built around `dream/config/viewmodel-rig.txt`.

This pass keeps the runtime model unchanged for the rest of the game while moving parsing, serialization, and file-bootstrap behavior into a focused helper.

## What Changed

- Added `dream/config/viewmodel-rig.txt` as the committed source config file.
- Added `ViewModelRigTextConfig` to create, parse, and serialize the startup config.
- Added `AssetPaths.Config()` so config resolution follows the existing runtime asset model.
- Updated `dream.csproj` to copy `config/**` into the build output.
- Updated `Game` startup to load the rig config before constructing `PlayScene`.
- Updated `GameSettings` so `ResetViewModelRig()` restores the startup-loaded baseline rather than recompiling hardcoded defaults.

## Runtime Behavior

Current behavior is:

- startup-load only
- auto-create the file if missing
- per-field fallback for invalid numeric values
- ignore unknown keys
- do not auto-save runtime menu changes back into the file

## Verification

Commands run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~ViewModelRigTextConfigTests" --nologo
dotnet test dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~GameSettingsTests" --nologo
dotnet build dream\dream.csproj --nologo
dotnet test dream.Tests\dream.Tests.csproj --nologo
```

Notes:

- The `dream` process had to be stopped before verification so the build output was no longer locked.
- Full verification passed after the startup config changes were wired in.
