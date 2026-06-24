# HUD Crosshair Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the off-center crosshair and make HUD layout viewport-driven, testable, and easier to maintain.

**Architecture:** The work will introduce a small HUD layout helper that computes responsive crosshair geometry from the live viewport, then update `Render` to rebuild its crosshair vertices from that layout rather than from baked coordinates. Tests will cover the layout math directly, and docs will capture the new HUD rules.

**Tech Stack:** C#/.NET 8, OpenTK, OpenGL, ImGui.NET, xUnit

---

### Task 1: Add testable HUD layout math

**Files:**
- Create: `dream/HudLayout.cs`
- Create: `dream.Tests/HudLayoutTests.cs`

- [ ] **Step 1: Write the failing layout tests**

```csharp
using Xunit;

public class HudLayoutTests
{
    [Fact]
    public void CreateCrosshair_Centers_On_Viewport_Midpoint()
    {
        var layout = HudLayout.CreateCrosshair(1440, 1024);

        Assert.Equal(720f, layout.CenterX);
        Assert.Equal(512f, layout.CenterY);
    }

    [Fact]
    public void CreateCrosshair_Scales_With_Viewport_Size()
    {
        var small = HudLayout.CreateCrosshair(800, 600);
        var large = HudLayout.CreateCrosshair(1920, 1080);

        Assert.True(large.ArmLength > small.ArmLength);
        Assert.True(large.Gap >= small.Gap);
    }

    [Fact]
    public void CreateCrosshair_Produces_Symmetric_Vertices()
    {
        var layout = HudLayout.CreateCrosshair(1600, 900);
        var vertices = layout.ToVertexArray();

        Assert.Equal(layout.CenterY, vertices[1]);
        Assert.Equal(layout.CenterY, vertices[3]);
        Assert.Equal(layout.CenterY, vertices[9]);
        Assert.Equal(layout.CenterY, vertices[11]);
    }
}
```

- [ ] **Step 2: Run the HUD layout tests to verify they fail**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter HudLayoutTests --nologo
```

Expected:

- test discovery succeeds
- tests fail because `HudLayout` does not exist yet

- [ ] **Step 3: Implement the minimal HUD layout helper**

```csharp
public readonly record struct HudLayout(
    float CenterX,
    float CenterY,
    float ArmLength,
    float Gap)
{
    public static HudLayout CreateCrosshair(int width, int height)
    {
        float safeWidth = Math.Max(width, 1);
        float safeHeight = Math.Max(height, 1);
        float smallerAxis = Math.Min(safeWidth, safeHeight);

        float armLength = Math.Clamp(smallerAxis * 0.018f, 8f, 18f);
        float gap = Math.Clamp(smallerAxis * 0.0075f, 3f, 8f);

        return new HudLayout(
            safeWidth / 2f,
            safeHeight / 2f,
            armLength,
            gap);
    }
}
```

- [ ] **Step 4: Add vertex generation to the helper**

```csharp
public float[] ToVertexArray()
{
    return new[]
    {
        CenterX - Gap - ArmLength, CenterY,
        CenterX - Gap, CenterY,

        CenterX + Gap, CenterY,
        CenterX + Gap + ArmLength, CenterY,

        CenterX, CenterY - Gap - ArmLength,
        CenterX, CenterY - Gap,

        CenterX, CenterY + Gap,
        CenterX, CenterY + Gap + ArmLength,
    };
}
```

- [ ] **Step 5: Run the HUD layout tests to verify they pass**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter HudLayoutTests --nologo
```

Expected:

- `HudLayoutTests` pass

- [ ] **Step 6: Commit the HUD layout helper**

```powershell
git add dream/HudLayout.cs dream.Tests/HudLayoutTests.cs
git commit -m "feat: add viewport-driven HUD layout helper"
```

### Task 2: Rebuild crosshair geometry from the viewport

**Files:**
- Modify: `dream/Render.cs`
- Modify: `dream/HudLayout.cs`

- [ ] **Step 1: Remove the baked `800 x 600` crosshair vertices from HUD initialization**

Replace the current `float[] vertices` block in `Render.InitializeHUD()` with an empty dynamic buffer setup so geometry is no longer locked to startup coordinates.

- [ ] **Step 2: Add a renderer helper that uploads crosshair vertices from `HudLayout`**

```csharp
private void UpdateCrosshairGeometry()
{
    float[] vertices = HudLayout.CreateCrosshair(_currentSize.X, _currentSize.Y).ToVertexArray();

    GL.BindBuffer(BufferTarget.ArrayBuffer, _hudVbo);
    GL.BufferData(
        BufferTarget.ArrayBuffer,
        vertices.Length * sizeof(float),
        vertices,
        BufferUsageHint.DynamicDraw);
    GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
}
```

- [ ] **Step 3: Rebuild crosshair geometry when viewport metrics change**

Call `UpdateCrosshairGeometry()`:

- after HUD initialization
- after projection or window-size updates
- after resolution changes triggered by the settings menu

- [ ] **Step 4: Keep drawing logic unchanged except for the new vertex source**

Retain:

- line-based crosshair rendering
- crosshair hidden while settings are visible

Do not mix centering math back into `DrawHUD()`.

- [ ] **Step 5: Run a full build**

Run:

```powershell
dotnet build dream\dream.csproj --nologo
```

Expected:

- build succeeds
- existing warnings may remain

- [ ] **Step 6: Commit the renderer-side geometry update**

```powershell
git add dream/Render.cs dream/HudLayout.cs
git commit -m "fix: rebuild crosshair from viewport layout"
```

### Task 3: Tighten HUD boundaries and layout usage

**Files:**
- Modify: `dream/Render.cs`

- [ ] **Step 1: Add a small accessor or local variable for current HUD layout**

Use a single layout calculation per update path so `Render` does not recompute or scatter size rules.

Example:

```csharp
HudLayout crosshairLayout = HudLayout.CreateCrosshair(_currentSize.X, _currentSize.Y);
```

- [ ] **Step 2: Keep ImGui panel placement explicitly separate from crosshair geometry**

Maintain one section for:

- OpenGL HUD primitives

And a separate section for:

- ImGui IO setup
- HUD text window
- settings menu

- [ ] **Step 3: Remove stale HUD comments that describe the old fixed-coordinate behavior**

Delete or rewrite comments that still reference:

- `390,300`
- `410,300`
- `400,290`
- `400,310`
- a fixed `800 x 600` HUD layout model

- [ ] **Step 4: Run focused tests and build verification**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter HudLayoutTests --nologo
dotnet build dream\dream.csproj --nologo
```

Expected:

- HUD layout tests pass
- build succeeds

- [ ] **Step 5: Commit the HUD cleanup**

```powershell
git add dream/Render.cs
git commit -m "refactor: clarify HUD layout responsibilities"
```

### Task 4: Update docs and ledger

**Files:**
- Modify: `README.md`
- Modify: `docs/rendering-and-assets.md`
- Modify: `docs/build-and-run.md`
- Create: `docs/ledger/work-log/2026-06-24-hud-crosshair-improvements.md`

- [ ] **Step 1: Update user-facing docs**

Document that:

- the crosshair is viewport-driven
- the HUD crosshair scales with window size
- future HUD positioning should not use hardcoded viewport pixel coordinates

- [ ] **Step 2: Record implementation and verification**

Add a work log with:

- summary of root cause
- files changed
- commands run
- observed results

- [ ] **Step 3: Commit the documentation update**

```powershell
git add README.md docs/rendering-and-assets.md docs/build-and-run.md docs/ledger/work-log/2026-06-24-hud-crosshair-improvements.md
git commit -m "docs: record HUD crosshair improvements"
```

### Task 5: Perform runtime verification

**Files:**
- Test: `dream/Render.cs`
- Test: `dream/HudLayout.cs`
- Test: `dream/dream.csproj`

- [ ] **Step 1: Launch the application**

Run:

```powershell
dotnet run --project dream\dream.csproj
```

- [ ] **Step 2: Verify the crosshair visually at multiple sizes**

Check:

- default `1440 x 1024` startup size
- at least one smaller preset such as `1280 x 720`
- at least one larger preset such as `1920 x 1080`

Expected:

- crosshair remains centered
- crosshair scales modestly rather than remaining fixed-size
- crosshair remains hidden while settings are open

- [ ] **Step 3: Re-check scope**

Confirm the implementation fixed HUD layout correctness without expanding into a full overlay redesign.
