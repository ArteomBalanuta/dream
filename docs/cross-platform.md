# Cross-platform client bundles

Shareable, self-contained desktop clients for win-x64 and linux-x64. Recipients install no SDK and no .NET runtime.

## Commands

Run from the repo root (needs `just`, Python and, for Windows bundles, the Visual Studio C++ redistributable).

```powershell
just package-windows          # artifacts\dist\dream-<version>-win-x64.zip
just package-linux            # artifacts\dist\dream-<version>-linux-x64.tar.gz (also built on Windows)
just package-all              # both, in parallel after one shared restore
just run-dist                 # run the newest packaged client from artifacts\dist
python -I tools/dev/archive_bundle.py --selftest
```

`<version>` defaults to `yyyyMMdd-<sha7>`; pass one with `just package-windows 1.2.3` or `tools\dev\package.ps1 -Rid all -Version 1.2.3`.
`tools/dev/package.ps1` runs in Windows PowerShell 5.1 and pwsh 7. It builds from a clean export of `HEAD`
(`git archive`, checked against `git ls-tree` for file count and bytes), never from the working tree: git-ignored and untracked
files under the copied asset folders (local OBJ files, `*.zip` packs, a world database in `dream/config`) cannot ship, and
uncommitted edits to tracked files are not in the bundle (the script warns when there are any). It publishes into
`artifacts/dist/<rid>/dream-<version>-<rid>/`, verifies it, packs it, verifies the archive.

Outputs:

- `artifacts/dist/<rid>/dream-<version>-<rid>/`: the stage folder (what is zipped).
- `artifacts/dist/dream-<version>-<rid>.zip|.tar.gz`: one top-level folder. In the tar.gz `dream` and `run-dream.sh` are mode 0755.
- `vcomp140.dll` source, resolved before any export or publish: `-Vcomp140 <path>`, then the `DREAM_VCOMP140` environment
  variable, then the Visual Studio redistributable found through vswhere, then `%SystemRoot%\System32\vcomp140.dll` (installed
  by the Visual C++ redistributable). Only Windows tries the last two; a host that finds none fails: a win-x64 bundle is never
  shipped without it.
- `artifacts/dist-src/<sha>/`: the export of `HEAD` with its own `obj\` and `bin\` (the checkout's `dream\bin` is never
  touched). One export serves both RIDs of a run.

Publish is `--self-contained` with no trimming, no single-file and no ReadyToRun: BulletSharp is a net40 assembly, content
JSON is loaded by reflection and ImGui/OpenTK resolve natives by name. The restore is shared and lists both RIDs
(`RuntimeIdentifiers=win-x64;linux-x64` through the environment, because a `;` list cannot be quoted the same way in
5.1 and 7), which is why the two parallel publishes do not race on `obj` (they only read the export's `project.assets.json`).

`archive_bundle.py verify` fails a bundle that lacks a required native (win: `libbulletc.dll`, `vcomp140.dll`, `cimgui.dll`,
`glfw3.dll`, `e_sqlite3.dll`, `OpenTK.Audio.OpenAL.dll`; linux: `libbulletc.so`, `libcimgui.so`, `libglfw.so.3`,
`libe_sqlite3.so`, `run-dream.sh`), content or shaders, or that contains `player-profiles.db*`, `account-tokens.json`,
`imgui.ini`, any `*.zip` (a local asset pack that leaked in) or the other platform's natives, or that is over a size cap
(900 MB uncompressed; the clean win-x64 bundle is about 585 MB, nearly all of it `animations/`).

## Linux runtime requirements

Not verified on this machine: the Linux bundle was built and inspected on Windows only (no WSL, no Linux host), so
whether it starts has not been tested. What it should need:

- libicu, libstdc++6, glibc 2.14 or newer (libbulletc).
- Mesa or a vendor driver with OpenGL 3.3 core.
- X11 or Wayland client libraries for GLFW 3.4 (bundled `libglfw.so.3`).
- libopenal1 for sound; without it the game should run silent. The OpenAL backend (`dream/Client/Audio/OpenALOutput.cs`) has
  never run on any machine: there is no OpenAL library here, so only the PCM conversion and the failure path are tested.
- Start with `./run-dream.sh`, which `cd`s into the bundle so `imgui.ini` and relative files land there.

For a read-only install location set `DREAM_DATA_DIR` to a writable folder: the world database, tokens and logs go
there instead of next to the program (`docs/configuration.md`, Run directories).

## Why BulletSharp stays

Bullet is load-bearing: the player is a rigid body in `dream/Simulation/Players/PlayerMotor.cs`, terrain and cube
colliders are bound in `dream/Simulation/World/WorldPhysicsBinder.cs`, and both the client
(`dream/Client/World/ClientWorldSession.cs`) and the server (`dream/Simulation/Runtime/WorldSessionRuntime.cs`) step it.
See `docs/ledger/decisions/2026-11-20-bullet-kept-for-cross-platform.md`.

How its natives are wired: the BulletSharp package's own `build` assets are excluded (`ExcludeAssets=build`) because they
copy `libbulletc` under platform-suffixed names into the wrong places; the project instead adds per-RID `None` items that
copy the right file as `libbulletc.dll` or `libbulletc.so` into any build or publish output.

- `vcomp140.dll`: `libbulletc.dll` imports `VCOMP140.DLL`, so a machine without the VC++ redistributable could not load
  physics. The Windows bundle copies it from `VC\Redist\MSVC\<ver>\x64\Microsoft.VC14*.OpenMP` (found through
  vswhere; packaging fails clearly when it is missing).
- `BulletProfilerGuard` (`dream/Simulation/Physics/BulletProfilerGuard.cs`) patches Windows x64 only. Linux keeps the latent
  crash when Bullet's profiler sees more than 63 threads. Follow-up: resolve `btGetCurrentThreadIndex` with `dlsym`.

## What removing Bullet would take

Replace the player capsule/rigid-body motor, the terrain and cube collision queries (ground, step, slide, ray casts used by
combat and aim), and the physics stepping in both client prediction and the authoritative server, then re-tune movement and
re-prove lockstep determinism with the simulation and net test tiers. A separate plan, not part of packaging.
