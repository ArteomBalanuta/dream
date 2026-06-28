Purpose: Define the remaining roadmap for broadening destructible and interactive world objects beyond placed cubes.
Status: Analysis
Scope: Props, interactables, richer destruction behavior, and broader world-object descriptors

# Goal

Expand the current generic world-object registration seam into practical support for non-cube props and interactables.

# What Exists Already

The repo now has:

- `WorldObjectRegistry`
- `WorldObjectRecord`
- cube damage and destruction
- removal through world/physics/registry coordination
- snapshot preservation across floor rebuilds

# What Is Missing

- actual non-cube world-object types using the registry in production
- per-object durability rules
- richer destruction outcomes
- broader descriptors for props or interactables

# Recommended Phases

## Phase 1: First Non-Cube Breakable Prop

Add one real prop using the same registry seam as cubes.

Good candidates:

- crate
- barrel
- simple decorative block with custom HP

The point is to prove that world-object combat participation is genuinely generic, not just generic in type names.

## Phase 2: Object Definition Layer

Add a lightweight definition shape for world objects, for example:

- base health
- anchor behavior
- optional destruction behavior
- optional visual/resource id

Keep it focused on the current gameplay need and avoid a huge data-driven asset system yet.

## Phase 3: Destruction Reactions

Add the first post-destruction consequences such as:

- spawn reaction
- scene event
- simple drop or debris hook

This is where world objects become more than HP containers.

## Phase 4: Interaction Growth

After destructibles are healthy, extend the same family toward:

- non-destructive interactables
- pickups
- world devices or triggerables

# Recommended File-Level Focus

- `dream/DestructibleCubeRegistry.cs` as the current home of `WorldObjectRegistry` types
- `dream/PlayScene.cs`
- `dream/Map.cs`
- new focused object-definition or object-factory types only when a second real object type exists

# Risks

- leaving everything effectively cube-shaped even after generic renaming
- mixing object-definition data and scene orchestration into one large type
- creating a heavy asset system before multiple real object families exist

# Recommendation

The next step should be one non-cube breakable prop with custom health and destruction handling. That gives the clearest signal about what abstractions are truly needed afterward.
