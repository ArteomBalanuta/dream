# Viewmodel Rig Text Config Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace hardcoded startup viewmodel rig defaults with a dedicated auto-created text config file that loads once at startup with per-field fallback behavior.

**Architecture:** Keep `ViewModelRigSettings` as the runtime model, add a focused `ViewModelRigTextConfig` helper for parsing and writing the `.txt` file, add an `AssetPaths` helper for config resolution, and bootstrap the loaded rig in `Game.cs` before scene creation. Runtime menu edits remain in-memory only for this first pass.

**Tech Stack:** C# 13 / .NET 10, xUnit, existing `AssetPaths` runtime path model, existing `GameSettings` startup flow, plain text key-value parsing.

---

## File Structure

### Existing files to modify

- `dream/AssetPaths.cs`
  - Add config-path resolution helper.
- `dream/Game.cs`
  - Bootstrap viewmodel rig config before creating `PlayScene`.
- `dream/GameSettings.cs`
  - Accept a startup-loaded `ViewModelRigSettings`.
- `dream/ViewModelRigSettings.cs`
  - Centralize built-in defaults used by the config bootstrap.
- `dream/dream.csproj`
  - Copy `config/**` into output.
- `dream.Tests/GameSettingsTests.cs`
  - Cover startup-loaded rig behavior.
- `docs/configuration.md`
  - Document config location and runtime behavior.
- `docs/build-and-run.md`
  - Document file creation and startup-load behavior.

### New files to create

- `dream/ViewModelRigTextConfig.cs`
  - Load, parse, and write `viewmodel-rig.txt`.
- `dream/config/viewmodel-rig.txt`
  - Canonical source config file committed to the repo.
- `dream.Tests/ViewModelRigTextConfigTests.cs`
  - Focused parser and file-bootstrap coverage.
- `docs/ledger/work-log/2026-06-28-viewmodel-rig-text-config.md`
  - Execution record after implementation.

## Task 1: Add config path support and a committed text config file

**Files:**
- Modify: `dream/AssetPaths.cs`
- Modify: `dream/dream.csproj`
- Create: `dream/config/viewmodel-rig.txt`

- [ ] **Step 1: Inspect the current path helper shape**

Run: `Get-Content dream\\AssetPaths.cs`
Expected: Existing helper methods that resolve runtime-owned content from `AppContext.BaseDirectory`.

- [ ] **Step 2: Add a config-path helper**

```csharp
using System;
using System.IO;

public static class AssetPaths
{
    public static string Config(string relativePath)
    {
        return Path.Combine(AppContext.BaseDirectory, "config", relativePath);
    }
}
```

- [ ] **Step 3: Add config folder copy rules to the project file**

```xml
<ItemGroup>
  <None Update="shaders\**\*;animations\**\*;fonts\**\*;config\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

- [ ] **Step 4: Create the committed text config file**

```txt
# Viewmodel rig startup defaults
body_anchor_spread=0.000
body_anchor_drop=0.306
body_anchor_forward_pull=0.180
held_item_position_x=-0.046
held_item_position_y=0.204
held_item_position_z=-0.156
held_item_rotation_x=0.938
held_item_rotation_y=1.406
held_item_rotation_z=-1.406
```

- [ ] **Step 5: Build to verify content-copy configuration is valid**

Run: `dotnet build dream\\dream.csproj --nologo`
Expected: `Build succeeded.` with the repo's existing warnings only.

## Task 2: Add the text config loader/writer with fallback behavior

**Files:**
- Create: `dream/ViewModelRigTextConfig.cs`
- Test: `dream.Tests/ViewModelRigTextConfigTests.cs`

- [ ] **Step 1: Write the failing config tests**

```csharp
using System.IO;
using OpenTK.Mathematics;
using Xunit;

public class ViewModelRigTextConfigTests
{
    [Fact]
    public void LoadOrCreate_Writes_Default_File_When_Missing()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(tempDir, "viewmodel-rig.txt");

        ViewModelRigSettings settings = ViewModelRigTextConfig.LoadOrCreate(path);

        Assert.True(File.Exists(path));
        Assert.Equal(0.306f, settings.BodyAnchor.VerticalDrop);
        Assert.Equal(new Vector3(-0.046f, 0.204f, -0.156f), settings.HeldItem.PositionOffset);
    }

    [Fact]
    public void LoadOrCreate_Overrides_Recognized_Values_From_File()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(tempDir, "viewmodel-rig.txt");
        File.WriteAllText(path, "body_anchor_drop=0.500\nheld_item_position_x=0.125\n");

        ViewModelRigSettings settings = ViewModelRigTextConfig.LoadOrCreate(path);

        Assert.Equal(0.500f, settings.BodyAnchor.VerticalDrop);
        Assert.Equal(0.125f, settings.HeldItem.PositionOffset.X);
        Assert.Equal(0.204f, settings.HeldItem.PositionOffset.Y);
    }

    [Fact]
    public void LoadOrCreate_Falls_Back_Per_Field_On_Invalid_Value()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(tempDir, "viewmodel-rig.txt");
        File.WriteAllText(path, "body_anchor_drop=bad\nheld_item_rotation_y=1.500\n");

        ViewModelRigSettings settings = ViewModelRigTextConfig.LoadOrCreate(path);

        Assert.Equal(0.306f, settings.BodyAnchor.VerticalDrop);
        Assert.Equal(1.500f, settings.HeldItem.RotationOffsetDegrees.Y);
    }
}
```

- [ ] **Step 2: Run the focused tests to verify they fail**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~ViewModelRigTextConfigTests" --nologo`
Expected: FAIL with missing config helper type.

- [ ] **Step 3: Implement the config helper**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OpenTK.Mathematics;

public static class ViewModelRigTextConfig
{
    public static ViewModelRigSettings LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Serialize(ViewModelRigSettings.CreateDefault()));
        }

        ViewModelRigSettings settings = ViewModelRigSettings.CreateDefault();
        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            ApplyValue(settings, parts[0], parts[1]);
        }

        return settings;
    }

    public static string Serialize(ViewModelRigSettings settings)
    {
        return string.Join(Environment.NewLine, new[]
        {
            "# Viewmodel rig startup defaults",
            $"body_anchor_spread={settings.BodyAnchor.HorizontalSpread.ToString(CultureInfo.InvariantCulture)}",
            $"body_anchor_drop={settings.BodyAnchor.VerticalDrop.ToString(CultureInfo.InvariantCulture)}",
            $"body_anchor_forward_pull={settings.BodyAnchor.ForwardPull.ToString(CultureInfo.InvariantCulture)}",
            $"held_item_position_x={settings.HeldItem.PositionOffset.X.ToString(CultureInfo.InvariantCulture)}",
            $"held_item_position_y={settings.HeldItem.PositionOffset.Y.ToString(CultureInfo.InvariantCulture)}",
            $"held_item_position_z={settings.HeldItem.PositionOffset.Z.ToString(CultureInfo.InvariantCulture)}",
            $"held_item_rotation_x={settings.HeldItem.RotationOffsetDegrees.X.ToString(CultureInfo.InvariantCulture)}",
            $"held_item_rotation_y={settings.HeldItem.RotationOffsetDegrees.Y.ToString(CultureInfo.InvariantCulture)}",
            $"held_item_rotation_z={settings.HeldItem.RotationOffsetDegrees.Z.ToString(CultureInfo.InvariantCulture)}"
        }) + Environment.NewLine;
    }

    private static void ApplyValue(ViewModelRigSettings settings, string key, string value)
    {
        if (!float.TryParse(value, CultureInfo.InvariantCulture, out float parsed))
        {
            return;
        }

        switch (key)
        {
            case "body_anchor_spread":
                settings.BodyAnchor.HorizontalSpread = parsed;
                break;
            case "body_anchor_drop":
                settings.BodyAnchor.VerticalDrop = parsed;
                break;
            case "body_anchor_forward_pull":
                settings.BodyAnchor.ForwardPull = parsed;
                break;
            case "held_item_position_x":
                settings.HeldItem.PositionOffset = settings.HeldItem.PositionOffset with { X = parsed };
                break;
            case "held_item_position_y":
                settings.HeldItem.PositionOffset = settings.HeldItem.PositionOffset with { Y = parsed };
                break;
            case "held_item_position_z":
                settings.HeldItem.PositionOffset = settings.HeldItem.PositionOffset with { Z = parsed };
                break;
            case "held_item_rotation_x":
                settings.HeldItem.RotationOffsetDegrees = settings.HeldItem.RotationOffsetDegrees with { X = parsed };
                break;
            case "held_item_rotation_y":
                settings.HeldItem.RotationOffsetDegrees = settings.HeldItem.RotationOffsetDegrees with { Y = parsed };
                break;
            case "held_item_rotation_z":
                settings.HeldItem.RotationOffsetDegrees = settings.HeldItem.RotationOffsetDegrees with { Z = parsed };
                break;
        }
    }
}
```

- [ ] **Step 4: Run the focused tests to verify they pass**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~ViewModelRigTextConfigTests" --nologo`
Expected: PASS.

## Task 3: Thread startup-loaded rig settings into `GameSettings`

**Files:**
- Modify: `dream/ViewModelRigSettings.cs`
- Modify: `dream/GameSettings.cs`
- Modify: `dream/Game.cs`
- Test: `dream.Tests/GameSettingsTests.cs`

- [ ] **Step 1: Write the failing startup-seam tests**

```csharp
[Fact]
public void Constructor_Uses_Injected_ViewModelRig()
{
    ViewModelRigSettings rig = ViewModelRigSettings.CreateDefault();
    rig.BodyAnchor.VerticalDrop = 0.500f;

    GameSettings settings = new(new Vector2i(1440, 1024), rig);

    Assert.Equal(0.500f, settings.ViewModelRig.BodyAnchor.VerticalDrop);
}
```

- [ ] **Step 2: Run the focused tests to verify they fail**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~GameSettingsTests" --nologo`
Expected: FAIL with constructor mismatch.

- [ ] **Step 3: Add a canonical default builder and injected settings constructor**

```csharp
using OpenTK.Mathematics;

public sealed class ViewModelRigSettings
{
    public static ViewModelRigSettings CreateDefault()
    {
        return new ViewModelRigSettings
        {
            HeldItem = new ViewModelSlotTuning
            {
                PositionOffset = new Vector3(-0.046f, 0.204f, -0.156f),
                RotationOffsetDegrees = new Vector3(0.938f, 1.406f, -1.406f)
            },
            BodyAnchor = new ViewModelBodyAnchorSettings
            {
                HorizontalSpread = 0.000f,
                VerticalDrop = 0.306f,
                ForwardPull = 0.180f
            }
        };
    }
}
```

```csharp
public GameSettings(Vector2i initialSize, ViewModelRigSettings? viewModelRig = null)
{
    ViewModelRig = viewModelRig ?? ViewModelRigSettings.CreateDefault();
    ApplyResolution(initialSize);
}
```

- [ ] **Step 4: Bootstrap config loading in `Game.cs`**

```csharp
ViewModelRigSettings viewModelRig = ViewModelRigTextConfig.LoadOrCreate(
    AssetPaths.Config("viewmodel-rig.txt"));
_settings = new GameSettings(new Vector2i(1440, 1024), viewModelRig);
```

- [ ] **Step 5: Run the focused tests to verify they pass**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~GameSettingsTests" --nologo`
Expected: PASS.

## Task 4: Document the config and record the work

**Files:**
- Modify: `docs/configuration.md`
- Modify: `docs/build-and-run.md`
- Create: `docs/ledger/work-log/2026-06-28-viewmodel-rig-text-config.md`

- [ ] **Step 1: Update configuration docs**

Add a short section explaining:

- `dream/config/viewmodel-rig.txt`
- runtime output copy behavior
- auto-create if missing
- startup-load only in this first pass

- [ ] **Step 2: Update build/run docs**

Add a short section explaining:

- where the file appears in output
- that deleting it will cause the game to recreate it on next launch
- that menu edits do not yet save back to file

- [ ] **Step 3: Add a work-log entry**

Record:

- the design intent
- the file path chosen
- the auto-create behavior
- the startup-only load model
- verification commands used

- [ ] **Step 4: Run full verification**

Run: `dotnet build dream\\dream.csproj --nologo`
Expected: `Build succeeded.` with existing warnings only.

Run: `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
Expected: PASS for the full suite.
