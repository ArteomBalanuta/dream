# Build And Run Documentation Design

**Purpose:** Define the intended structure and content for a dedicated build/run guide for the `dream` project.

**Status:** Drafted for review

**Scope:** A new `docs/build-and-run.md` file plus a small README link update

## Goal

Create a focused operational guide that explains how to build, test, and run the project cleanly from the repository root without bloating the top-level `README.md`.

## Chosen Approach

Use a dedicated document at:

- `docs/build-and-run.md`

and link it from:

- `README.md`

## Why This Approach

- Keeps `README.md` concise for first-time readers
- Gives the repository one clear operational guide for repeated use
- Lets the build/test/run instructions evolve without overloading the repo landing page

## Intended Content

The document should include:

### 1. Prerequisites

- `.NET 8 SDK`
- Windows desktop environment with OpenGL support

### 2. Repository Root Usage

State that commands are intended to be run from the repository root.

### 3. Clean Build

Document:

```powershell
dotnet build dream\dream.csproj
```

and note that the build currently succeeds with known warnings.

### 4. Run

Document:

```powershell
dotnet run --project dream\dream.csproj
```

and explain that runtime assets are copied to output and resolved through `AssetPaths`.

### 5. Tests

Document:

```powershell
dotnet test dream.Tests\dream.Tests.csproj
```

### 6. Known Warnings

State the currently expected warning categories:

- `BulletSharp` compatibility warning against `net8.0`
- `SixLabors.ImageSharp` advisory warnings

### 7. Generated Local Files

Note that local runtime artifacts such as:

- `imgui.ini`
- `dream-run*.log`

are ignored and should not be treated as source files.

### 8. Troubleshooting

Include concise help for:

- missing `.NET 8 SDK`
- build warnings versus build errors
- runtime asset problems if output folders are missing

## README Change

`README.md` should gain a link to `docs/build-and-run.md` under the existing further-reading section or another short navigation area.

## Style

The guide should be:

- practical
- command-first
- concise
- specific to the current repository state

It should avoid:

- repeating all architecture content
- broad troubleshooting that is not grounded in this repo

## Success Condition

A new contributor or future agent should be able to open one file and quickly answer:

- how to build
- how to run
- how to test
- what warnings are currently expected
