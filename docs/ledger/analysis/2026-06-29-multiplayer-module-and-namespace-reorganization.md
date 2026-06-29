# Multiplayer Module And Namespace Reorganization

Purpose: define the safest structure-improvement direction for the growing multiplayer code in `dream` without taking on a destabilizing big-bang architecture rewrite.
Status: complete
Scope: folders, namespaces, seam boundaries, incremental migration order, and test-hardening implications

## Recommendation

Use an incremental C# folder-and-namespace split around the multiplayer subsystem rather than a full onion/MVP rewrite across the whole repo in one pass.

That means:

- keep current gameplay/rendering systems stable while multiplayer boundaries are still evolving
- group multiplayer code by responsibility
- add namespaces and folder movement in bounded slices
- keep tests moving with each slice

## Why Not A Big-Bang Rewrite

The repo is already mid-transition:

- startup flow is now multiplayer-first
- protocol and transport seams exist
- the runtime is partially session-aware
- some client code still assumes one controlled runtime actor

A full architecture inversion right now would move too many files before the multiplayer seams are actually settled.

## Best-Fit Structure Direction

### Recommended top-level multiplayer grouping

- `dream/Multiplayer/Protocol`
- `dream/Multiplayer/Client`
- `dream/Multiplayer/Server`
- `dream/Multiplayer/Runtime`
- `dream/Multiplayer/Diagnostics`

### Keep outside multiplayer for now

- rendering
- assets
- generic gameplay primitives
- world/map/cube systems
- physics core

## Namespace Direction

Use namespaces that mirror the new folders, for example:

- `Dream.Multiplayer.Protocol`
- `Dream.Multiplayer.Client`
- `Dream.Multiplayer.Server`
- `Dream.Multiplayer.Runtime`

Do not try to namespace-convert the entire repo at once.

Start with files that are already multiplayer-specific, then migrate consumers gradually.

## First Structural Seams To Stabilize Before Moving Files

### 1. Session actor registry seam

Replace the current split knowledge across:

- `PlayerSessionRegistry`
- `_secondaryRemotePlayers`
- primary-player helper methods inside `WorldSessionRuntime`

with one authoritative session-actor lookup seam.

### 2. Controlled actor facade

Client-side systems should depend on a controlled-actor abstraction instead of directly on `runtime.Player`.

### 3. Shared player-target seam

This slice has already started through `IAuthoritativePlayerTarget`.

That seam should stay small and runtime-facing, then become the basis for later registry and correction cleanup.

## Testing Direction

Structure cleanup should add missing tests alongside each slice, especially for:

- join semantics and actor ownership
- actor id stability across snapshots
- primary/global versus per-actor interaction mode transition behavior
- client correction scoped to the controlled session actor
- multi-player PvE target selection and damage application

## Migration Order

1. stabilize session-actor runtime seams
2. add the controlled-actor client seam
3. move multiplayer files into grouped folders
4. add namespaces for moved multiplayer files
5. clean up imports/usings and redundant adapter code

## Conclusion

The best fitting structure change for this repo is an incremental multiplayer module split with matching namespaces and test-backed seam extraction.

That gives the project clearer “packages” in the C# sense without paying the risk of rewriting the whole game architecture while multiplayer ownership is still changing.
