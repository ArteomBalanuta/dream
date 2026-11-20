# 2026-11-20: cross-platform client and shareable bundles

Fast-forwarded into `master` at `3f1e6dfc` (from `4d404c81`); not pushed.

## What was built

- **Bullet natives on every platform.** `dream/dream.csproj` excludes the BulletSharp package's build assets (they copied
  the Windows dll as `libbulletc.dll` on every OS and added nothing to publish) and ships `libbulletc.dll` (win-x64) and
  `libbulletc.so` (linux-x64) itself, so .NET's default probing finds them; a RID-less build ships both. Stale
  platform-suffixed copies are deleted from existing `bin` folders. The test project's copy-from-`dream\bin` target is
  gone, so `build-verify` no longer needs a normal build first. BulletSharp stays: it is the player's rigid body and the
  terrain colliders on client and server (`docs/ledger/decisions/2026-11-20-bullet-kept-for-cross-platform.md`).
- **OpenAL audio.** `AudioOutputFactory` picks WinMM (NAudio) on Windows and the new `OpenALOutput` elsewhere;
  `DREAM_AUDIO_BACKEND=winmm|openal` overrides. Opening is bounded (5 s) and failure means silence, never a hang.
- **Bundles.** `just package-windows|package-linux|package-all|run-dist`, `tools/dev/package.ps1` and
  `tools/dev/archive_bundle.py`: self-contained publish from a `git archive` export of `HEAD`, `vcomp140.dll` added to
  win-x64 (`libbulletc.dll` imports it), verified file lists, `.zip` / `.tar.gz` with exec bits. About 378 MB each,
  mostly `animations/`.
- Docs: `docs/cross-platform.md`, README "Shareable Builds", `tools/dev/README.md`, an AGENTS.md capability row.

## Verification

- Full suite on `9ef2d532`: 10923 total, 10876 passed, 0 failed, 47 skipped. Audio filter on `3f1e6dfc`: 87/0.
- `just package-all` on `3f1e6dfc`: both bundles built and verified; the extracted win-x64 bundle passes
  `--validate-content`, creates a world with `--worlds list`, and a capture from it shows the player walking on the ground.
- `archive_bundle.py --selftest` (now in `pygates.ps1` quick) passes, including its negatives.

## Not verified

- The linux-x64 bundle has never been started (no Linux host or WSL here); the OpenAL backend has never played sound on
  any machine. How to confirm: `docs/cross-platform.md`, Status.
- Linux keeps Bullet's profiler thread-index crash unpatched (`BulletProfilerGuard` is Windows x64 only).
- `package.ps1` has not run under pwsh 7 or on a Linux host.
