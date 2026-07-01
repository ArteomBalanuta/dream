Purpose: define durable world-unit and imported-asset scale rules for Dream
Status: accepted
Scope: cube units, glTF world actors, player/enemy visual envelopes, collision alignment, and future asset metadata

## Decision

Dream uses one world unit as one cube side length.

A normal solid cube occupies a `1 x 1 x 1` world-unit cell. Gameplay positions are in cube-relative world units, with `Y` as up. Imported glTF assets are not allowed to define gameplay scale implicitly. They must be normalized through explicit asset metadata before being used as world actors.

## Canonical Actor Targets

- Player visual target: `1.2` cubes tall.
- Player neutral standing width target: `1.0` cube or less.
- Player neutral standing depth target: `1.0` cube or less.
- Player feet or contact base should sit on the floor plane, not at actor body center.
- Enemy and prop targets must be stated per asset in cube units before render-scale changes land.

The player target should stay compact beside the current wolf and read clearly inside the cube world: roughly `2x` the wolf target height and no more than one cube wide/deep in a neutral stance.

## glTF Import Rules

- Treat glTF authored dimensions as asset-local authored units, not gameplay units.
- Measure authored bounds after applying glTF node-local transforms.
- Convert authored bounds into world scale using explicit metadata stored near the asset definition.
- Prefer uniform scale for animated/skinned actors unless a future asset has a documented reason to use non-uniform scale.
- Keep visual normalization separate from collision and gameplay reach. Visual scale may inform collision defaults, but collision values must remain explicit.
- Do not hide scale fixes inside renderer-only offsets or camera hacks.

## Grounding Rules

- World actor model matrices should place the actor's intended foot/base point on the gameplay floor or actor position reference.
- If an asset is authored around its center, the asset definition must declare the body-center-to-foot or base offset.
- Grounding must be shared by local and remote renderers so host/new-client presentation does not diverge.

## Test Rules

World asset definitions should have tests that verify:

- model paths resolve under the repo-owned runtime asset folders
- required clips and nodes exist
- authored bounds or authored dimensions are recorded
- resolved scale produces the intended cube-relative target height
- the grounded model matrix places the base at the actor floor reference

## Rationale

The project now mixes cube geometry, player models, first-person viewmodels, and imported animated creatures. Without one unit convention, fixes drift into arbitrary scale constants, camera offsets, and per-renderer tweaks. A cube-relative policy gives us a shared language for gameplay, rendering, and multiplayer presentation.
