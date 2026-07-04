# Project Structure

## Top Level

- `dream/`: the main C# project directory
- `dream.Tests/`: xUnit test project for low-harness gameplay and runtime seam coverage
- `docs/`: documentation files for the project
- `skills/`: repo-local agent skills for recurring Dream workflows
- `Directory.Packages.props`: centrally managed NuGet package versions
- `global.json`: pinned .NET SDK version for consistent local builds
- `.gitignore`: repository ignore rules

## Solution And Project Files

- `dream/dream.sln`: Visual Studio solution file
- `dream/dream.csproj`: .NET project file with package references and build settings
- `dream.Tests/dream.Tests.csproj`: test project for focused regression coverage around gameplay and runtime seams
- `dream.Tests/dream.Tests.csproj` also copies the game's Bullet native runtime files into the test output so runtime-backed multiplayer tests can execute under `dotnet test`

## Source Layout

Production code uses the `Dream` root namespace and folder-matched file-scoped namespaces. The project intentionally remains one executable project for now; folders expose ownership before a later multi-project split.

- `dream/Application/`: process startup, OpenTK window shell, settings, runtime state, and title helpers
- `dream/Client/Startup/`: boot-time connection scene, connection form state, local integrated server handle, and connection coordinator
- `dream/Client/Scenes/`: desktop gameplay scene composition
- `dream/Client/Input/`: desktop input capture, text input queueing, and one-shot/resume filters
- `dream/Client/Networking/`: remote client session, client connector, and client transport adapter seams
- `dream/Client/Replication/`: client-side application of authoritative snapshots, corrections, action results, world mutations, and enemy state
- `dream/Client/Presentation/`: client-owned remote/local player presentation DTOs, interpolation buffers, and third-person camera rig
- `dream/Server/`: server-mode bootstrap, dedicated host/service loop, per-connection pump, and tick-driven host contract
- `dream/Multiplayer/Protocol/`: protocol message contracts, action/result payloads, routing, JSON codec, and length-prefixed framing
- `dream/Multiplayer/Transport/`: TCP transport implementations and transport connection abstractions
- `dream/Multiplayer/Sessions/`: player session identity, joined-player registry, roster entries, and command inboxes
- `dream/Multiplayer/Diagnostics/`: bounded authoritative flow traces and multiplayer diagnostics
- `dream/Simulation/Runtime/`: authoritative session runtime, tick helpers, runtime presentation state, session action contexts, and scene event hub
- `dream/Simulation/World/`: map/grid storage, cubes, world snapshots, mutations, world object registry, spatial indexing, and visibility
- `dream/Simulation/Players/`: player facade, camera, movement, interaction, command intents, combatant, and stat models
- `dream/Simulation/Enemies/`: enemy actors, registry, movement/collision helpers, combat adapter, and wolf spawn policy
- `dream/Simulation/Combat/`: shared combat identity, health, damage receipts/events, and hit resolution
- `dream/Simulation/Physics/`: Bullet integration, physics body factories, sync, and Bullet/OpenTK conversion extensions
- `dream/Rendering/Core/`: OpenGL render facade, shaders, resource cache, and model asset cache
- `dream/Rendering/Models/`: glTF model loading, animation playback, skinning/model transform helpers, and model asset definitions
- `dream/Rendering/World/`: world rendering, cube transforms, batch planning, and collision debug rendering
- `dream/Rendering/Actors/`: world-space actor renderers, actor labels, wolf animation state, and player model render state
- `dream/Rendering/Hud/`: HUD rendering, settings request DTOs, floating combat text, and authoritative action feedback
- `dream/Rendering/ViewModels/`: first-person viewmodel catalog, rig settings, pose resolution, controller, and renderer
- `dream/Assets/`: runtime path helpers for shaders, models, fonts, config, and other assets
- `dream/Configuration/`: text config load/save models
- `dream/Utilities/`: small generic helpers with no richer subsystem ownership

The test project mirrors the production folders under the `Dream.Tests` namespace. Structural tests in `dream.Tests/ProjectStructure/` guard against returning to a flat source root and protect the most important server/protocol dependency directions.

## Shader Files

Shader files live under `dream/shaders/`:

- `vertexShader.glsl`
- `fragmentShader.glsl`
- `cubeVertex.glsl`
- `cubeFragment.glsl`
- `hudVertex.glsl`
- `hudFragment.glsl`
- `hudVertexText.glsl`
- `hudFragmentText.glsl`

## Asset Files

Assets currently live under `dream/animations/` and `dream/fonts/`.

### `dream/animations/`

- `GrassTextureCube.gltf`: glTF model used for solid floor cubes
- `GrassTextureCube.bin`: binary data referenced by the glTF asset
- `grass_top.png`
- `Grass_bottom.jpg`
- `grass_block_side_texture.png`
- `me.png`
- `enemies/`: gameplay-facing creature assets such as `wolf.glb`
- `players/`: Kenney blocky player model, texture dependency, and copied CC0 license for remote-player rendering
- `viewmodels/`: first-person blockout hands and held-item assets
- `library/`: curated source vault for reusable CC0 packs and textures

### `dream/animations/library/`

- `quaternius/animals/`: Quaternius source creatures used as future creature-asset references
- `quaternius/characters/`: Quaternius character source pack already staged for future actor work
- `quaternius/fantasy-props-megakit/`: Quaternius medieval/fantasy prop library with weapons, tools, vegetables, stalls, furniture, and related textures/licenses
- `quaternius/stylized-nature-megakit/`: Quaternius stylized environment library with trees, plants, flowers, rocks, grass, and related textures in `glTF`, `FBX`, and `OBJ`
- `quaternius/survival-pack/`: Quaternius utility prop library with `Blends`, `FBX`, and `OBJ` exports for tools, weapons, camp gear, and general-purpose items
- `kenney/animated-characters-retro/`: Kenney animated stylized character source pack
- `kenney/food-kit/`: Kenney food-and-prop library for consumables, fruit, dishes, and kitchen set dressing
- `kenney/nature-kit/`: Kenney nature/environment pack used as a reusable source library
- `polyhaven/`: terrain texture source folders used by world-material rendering
- `skyboxes/cloudy-skyboxes/`: OpenGameArt CC0 skybox library with both horizontal-cross cubemaps and equirectangular panoramas for day, sunset, and night skies

### `dream/fonts/`

- `Arial.ttf`

## Documentation Files

- `README.md`: quickstart and high-level entry point
- `docs/overview.md`: high-level project summary
- `docs/architecture.md`: subsystem interaction guide
- `docs/project-structure.md`: repository and file guide
- `docs/configuration.md`: framework, packages, build, and runtime assumptions
- `docs/rendering-and-assets.md`: rendering path and asset usage details
- `docs/performance-and-optimizations.md`: current optimization techniques, guardrails, and known performance gaps

## Agent Skill Files

- `skills/creating-dream-skills/`: concise workflow for adding or refining repo-local Dream skills
- `skills/profiling-dream-multiplayer/`: repeatable server/client launch and diagnostics workflow for multiplayer performance and visibility investigations
- `skills/updating-dream-persistence-state/`: DDL-aware SQLite inspection and repair workflow for authoritative player, enemy, item, cube, and inventory persistence
