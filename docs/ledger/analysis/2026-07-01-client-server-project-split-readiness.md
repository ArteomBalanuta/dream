Purpose: evaluate whether Dream is ready to split source folders into multiple C# projects
Status: proposed follow-up
Scope: client/server/shared project boundaries after namespace migration

## Candidate Projects

- `Dream.Shared`: protocol DTOs, sessions, transport-neutral snapshot contracts.
- `Dream.Simulation`: authoritative runtime, world, player, enemy, combat, physics.
- `Dream.Rendering`: OpenGL rendering, model loading, HUD, viewmodels.
- `Dream.Client`: desktop app shell, startup UI, client networking, client replication, scenes.
- `Dream.Server`: headless server app and dedicated server service.

## Readiness Checklist

- Server dependency-direction tests are green.
- Protocol dependency-direction tests are green.
- Client-only OpenTK windowing and ImGui references are isolated under `Dream.Client` or `Dream.Rendering`.
- Asset copy rules are documented for any project that needs runtime assets.
- The executable apphost behavior remains documented and tested.

## Recommendation

Do not split projects until this checklist is green. The folder/namespace migration should land first because it exposes the remaining cross-boundary references cheaply.
