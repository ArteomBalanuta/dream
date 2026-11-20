Purpose: record why BulletSharp stays in the cross-platform client and how its natives are shipped
Status: accepted
Scope: physics dependency, native library packaging for win-x64 and linux-x64 bundles

## Decision

Dream keeps BulletSharp (managed wrapper over Bullet's `libbulletc`) for the Linux client instead of replacing the physics layer first.

Bullet is not a decoration: the player is a Bullet rigid body (`dream/Simulation/Players/PlayerMotor.cs`), terrain and cube colliders are Bullet shapes bound by `dream/Simulation/World/WorldPhysicsBinder.cs`, and both the client (`dream/Client/World/ClientWorldSession.cs`) and the server (`dream/Simulation/Runtime/WorldSessionRuntime.cs`) step it. Replacing it changes movement feel and lockstep determinism, which is a separate project with its own tests.

BulletSharp 0.11.1 ships `libbulletc` for Linux x64 inside the package, so the Linux client needs no native build of our own. The package's `build` assets are excluded and the natives are copied per RID as `libbulletc.dll` / `libbulletc.so` (workstream W1).

## Consequences

- Windows bundles also ship `vcomp140.dll` (the OpenMP runtime `libbulletc.dll` imports), so recipients need no Visual C++ redistributable.
- `BulletProfilerGuard` patches the Windows x64 thread-index lookup only. Linux keeps the latent crash when Bullet's profiler sees more than 63 threads. Follow-up: resolve `btGetCurrentThreadIndex` through `dlsym`.
- Bundles are self-contained (`dotnet publish --self-contained`, no trimming, no single file: BulletSharp targets net40 and the JSON, ImGui and OpenTK code relies on reflection).
- Removing Bullet later is possible but is its own plan; see `docs/cross-platform.md`.
