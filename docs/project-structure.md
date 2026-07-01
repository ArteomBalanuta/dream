# Project Structure

## Top Level

- `dream/`: the main C# project directory
- `dream.Tests/`: xUnit test project for low-harness gameplay and runtime seam coverage
- `docs/`: documentation files for the project
- `Directory.Packages.props`: centrally managed NuGet package versions
- `global.json`: pinned .NET SDK version for consistent local builds
- `.gitignore`: repository ignore rules

## Solution And Project Files

- `dream/dream.sln`: Visual Studio solution file
- `dream/dream.csproj`: .NET project file with package references and build settings
- `dream.Tests/dream.Tests.csproj`: test project for focused regression coverage around gameplay and runtime seams
- `dream.Tests/dream.Tests.csproj` also copies the game's Bullet native runtime files into the test output so runtime-backed multiplayer tests can execute under `dotnet test`

## Core Source Files

- `dream/Program.cs`: application entry point, explicit startup mode selector, desktop default path, and `ServerBootstrap` server-mode branch
- `dream/Game.cs`: OpenTK game window shell, desktop scene switching, integrated local server ownership, resize callback, and FPS tracking
- `dream/StartupConnectionScene.cs`: primary multiplayer boot scene that owns connect-form state and transitions into gameplay after a successful session handoff
- `dream/StartupMenuRenderer.cs`: startup-only ImGui renderer for the boot-time multiplayer connect UI
- `dream/PlayScene.cs`: desktop client scene/presenter that captures local input, advances `WorldSessionRuntime`, renders from runtime presentation state, applies client-local HUD/settings consequences, and owns the first client protocol session seam
- `dream/ClientConnectionOptions.cs`: validated host/port/player-name input model for startup multiplayer connection flow
- `dream/ConnectionUiRequest.cs`: startup-menu action/result DTO for edited input and button presses
- `dream/ConnectionFlowState.cs`: startup multiplayer connection-state enum
- `dream/NetworkSessionCoordinator.cs`: startup lifecycle seam that coordinates local server readiness, client connection attempts, and gameplay handoff
- `dream/WorldSessionRuntime.cs`: extracted authoritative runtime seam for the current desktop session; owns simulation state, update order, world rebuild behavior, and combat/build resolution
- `dream/WorldSessionPresentationState.cs`: presentation-facing snapshot contract used by the client scene to render authoritative world and HUD state without owning simulation truth
- `dream/PlayerSessionId.cs`: stable session identity value for one connected client controlling one authoritative player actor
- `dream/ConnectedPlayer.cs`: narrow runtime ownership record for a connected player session
- `dream/PlayerSessionRegistry.cs`: active-session registry with actor ownership and last-accepted command sequence tracking
- `dream/RemoteSessionPlayerState.cs`: lightweight authoritative remote-session actor state used for additional joined players, now including session-owned mode and first-pass combat state ahead of the full multi-body runtime
- `dream/AuthoritativeSessionActionContext.cs`: per-session authoritative action-validation context that keeps one-shot build/combat decisions decoupled from one global player-mode assumption and carries authoritative combat-aim state
- `dream/GameSettings.cs`: runtime configuration, current runtime mode, and settings-change events
- `dream/RuntimeState.cs`: compact gameplay-vs-settings runtime state model
- `dream/GameEvents.cs`: targeted configuration/runtime event payloads
- `dream/Player.cs`: gameplay facade for movement, camera-facing state, collision logging, and upward-facing build-use request capture
- `dream/PlayerController.cs`: raw desktop input-to-command translation
- `dream/PlayerCommandFrame.cs`: transport-safe per-session gameplay command DTO with sequence numbering
- `dream/PlayerMoveIntent.cs`: directional movement intent DTO
- `dream/PlayerLookIntent.cs`: look delta intent DTO
- `dream/PlayerModeSwitchIntent.cs`: mode-selection intent DTO
- `dream/ClientSimulationCommandPolicy.cs`: remote-authority seam that decides how much of a captured local command frame is allowed to drive the in-process client runtime
- `dream/TransportEnvelope.cs`: versioned protocol envelope metadata for client/server messages
- `dream/IProtocolMessage.cs`: common protocol message contract
- `dream/IClientToServerMessage.cs`: marker for client-originated protocol messages
- `dream/IServerToClientMessage.cs`: marker for server-originated protocol messages
- `dream/ClientHelloMessage.cs`, `dream/ClientJoinRequestMessage.cs`, `dream/PlayerCommandMessage.cs`, `dream/PlayerActionMessage.cs`: first-pass client-to-server message family
- `dream/ServerHelloMessage.cs`, `dream/JoinAcceptedMessage.cs`, `dream/JoinRejectedMessage.cs`, `dream/ServerSnapshotMessage.cs`, `dream/ServerActionMessage.cs`: first-pass server-to-client message family
- `dream/IPlayerActionPayload.cs`, `dream/BuildUseIntent.cs`, `dream/CombatUseIntent.cs`: typed one-shot action payload contracts
- `dream/IActionResultPayload.cs`, `dream/AuthoritativeActionResult.cs`, `dream/WorldMutationResult.cs`, `dream/CombatResolutionResult.cs`, `dream/ClientCorrectionDelta.cs`, `dream/RejectedUseDelta.cs`: typed authoritative action result and correction/rejection contracts
- `dream/PlayerActionHandler.cs`: server-side handler that validates authoritative action preconditions and returns `ServerActionMessage` results
- `dream/IPlayerCommandInbox.cs`, `dream/LatestPlayerCommandInbox.cs`: focused server-side command-buffer seam used to feed the latest joined-session command batch into authoritative ticks
- `dream/IProtocolCodec.cs`, `dream/TransportJsonCodec.cs`: protocol serialization seam plus JSON implementation
- `dream/IMessageFramer.cs`, `dream/LengthPrefixedMessageFramer.cs`: stream framing seam plus first-pass length-prefixed implementation
- `dream/IProtocolRouter.cs`, `dream/ProtocolRouter.cs`: focused message router seam and default router implementation
- `dream/IClientMessageHandler.cs`, `dream/IServerMessageHandler.cs`: role-specific protocol handler contracts
- `dream/ClientProtocolSession.cs`: client-side protocol intake seam with focused routing context
- `dream/RemoteClientOptions.cs`: env-driven remote-client configuration model for the desktop TCP path
- `dream/IRemoteSessionConnector.cs`, `dream/RemoteSessionConnector.cs`: connection seam and default adapter used by the startup coordinator to create `RemoteClientSession`
- `dream/RemoteClientSession.cs`: desktop client transport session that connects, sends hello/join, forwards command frames, and queues inbound authoritative messages
- `dream/IReplicatedPlayerTarget.cs`, `dream/ClientCorrectionApplier.cs`: local-player and interaction-mode authoritative snapshot application seam for remote client mode
- `dream/ClientAuthoritativeActionApplier.cs`: remote-client authoritative action-result intake seam for immediate accepted-result application plus rejected-result correction/event publication
- `dream/AuthoritativeActionRejectedEvent.cs`: client-scene event payload for surfaced authoritative action rejections
- `dream/AuthoritativeActionFeedbackSystem.cs`: scene-local rejection-feedback state used to surface short-lived authoritative-action failure text in the HUD
- `dream/AuthoritativeFlowTrace.cs`, `dream/AuthoritativeFlowTraceEntry.cs`: bounded authoritative action/snapshot diagnostics history contracts
- `dream/IMultiplayerDiagnosticsSink.cs`, `dream/MultiplayerDiagnostics.cs`: scene-owned diagnostics seam for recording authoritative snapshot and action intake facts without leaking diagnostics policy into runtime mutation helpers
- `dream/IReplicatedSessionStateTarget.cs`: narrow replicated-session metadata seam for authoritative mode-style snapshot state
- `dream/RemoteWorldMutationApplier.cs`: authoritative world snapshot and mutation application seam for remote client mode
- `dream/RemoteEnemyStateApplier.cs`: authoritative enemy snapshot application seam for remote client mode
- `dream/ClientScenePresenter.cs`, `dream/RemotePlayerPresentationState.cs`: client-owned remote-player presentation seam and interpolated render-state contract
- `dream/RemotePlayerSnapshotSample.cs`, `dream/RemotePlayerInterpolationBuffer.cs`, `dream/InterpolatedActorPose.cs`: remote-player interpolation primitives for client-side rendering
- `dream/CameraViewMode.cs`, `dream/ThirdPersonCameraRig.cs`: client-local first-person / third-person camera presentation mode and follow-camera math
- `dream/LocalPlayerPresentationState.cs`, `dream/LocalPlayerRenderer.cs`: world-space local player model rendering used when third-person view is enabled
- `dream/RemotePlayerRenderer.cs`: dedicated world-space renderer for remote players, kept separate from cube geometry and first-person presentation paths
- `dream/DedicatedServerHost.cs`: headless protocol/server composition root for multiplayer host work
- `dream/DedicatedServerService.cs`: long-running dedicated-server process loop that accepts clients, advances ticks, and broadcasts snapshots
- `dream/ILocalServerHandle.cs`, `dream/IntegratedLocalServerHandle.cs`: desktop-owned local authoritative server lifecycle seam used by the startup connection flow
- `dream/ServerConnection.cs`: per-connection framed protocol pump used by the dedicated server loop
- `dream/ITickDrivenAuthoritativeSessionHost.cs`: server-host interface for authoritative runtimes that can be advanced by the fixed tick driver
- `dream/ServerBootstrap.cs`: server-mode startup composition seam
- `dream/IServerTransport.cs`: dedicated-server transport abstraction used by the live host loop
- `dream/IClientTransport.cs`, `dream/TcpClientConnector.cs`: desktop-client connection abstraction and TCP implementation
- `dream/TcpClientTransport.cs`, `dream/TcpServerTransport.cs`, `dream/ITransportConnection.cs`: first concrete TCP transport edge and underlying connection abstraction
- `dream/SimulationClock.cs`: authoritative fixed-step accumulation helper for 64 Hz server cadence
- `dream/ServerTickRunner.cs`: fixed-tick driver that advances authoritative simulation independently from render cadence
- `dream/ActorSnapshot.cs`: transport-neutral authoritative actor snapshot DTO, including optional per-actor interaction mode for player actors
- `dream/WorldSnapshot.cs`: transport-neutral world snapshot DTO for join/resync style export
- `dream/WorldMutationDelta.cs`: transport-neutral world mutation delta DTO
- `dream/DamageEventDelta.cs`: transport-neutral combat/damage delta DTO
- `dream/ServerSnapshotEnvelope.cs`: authoritative snapshot envelope with tick metadata, current interaction mode, actor state, and pending deltas
- `dream/PlayerMotor.cs`: rigid-body movement, jump, and freeze behavior
- `dream/PlayerCameraRig.cs`: body-to-camera synchronization and mouse-look updates
- `dream/PlayerInteraction.cs`: edge-triggered gameplay interaction capture that turns plain data into `BuildUseRequest`
- `dream/PlayerCommands.cs`: small command/tuning records used to move desktop input across the runtime seam
- `dream/Map.cs`: world facade that composes grid storage, generation, physics registration, and accepted-placement reporting through `AddCube(...)`
- `dream/WorldGrid.cs`: sparse cube cell storage and bounds checks
- `dream/WorldGenerator.cs`: solid-cube creation and visual asset binding
- `dream/WorldPhysicsBinder.cs`: Bullet rigid-body registration for floor and cube world pieces
- `dream/Cube.cs`: cube data model used by the world
- `dream/PhysicsIntegrator.cs`: Bullet world setup, simulation stepping, explicit player-body registration ownership, body collection ownership, and disposal
- `dream/PhysicsFactory.cs`: factory for gameplay rigid bodies
- `dream/PhysicsSyncSystem.cs`: Bullet-to-world transform synchronization for dynamic cubes
- `dream/Camera.cs`: view direction and mouse-look behavior

## Rendering And Utility Files

- `dream/Render.cs`: OpenGL resource owner and rendering facade
- `dream/WorldRenderer.cs`: scene-facing world draw orchestration
- `dream/DebugRenderer.cs`: scene-facing collision debug draw orchestration
- `dream/HudRenderer.cs`: scene-facing HUD/settings draw orchestration
- `dream/Shader.cs`: shader source loading, OpenGL shader compilation, uniform setters, and uniform-location caching
- `dream/AnimatedModel.cs`: glTF mesh/material/texture loading and model rendering
- `dream/ModelAssets.cs`: shared model asset accessors
- `dream/ResourceCache.cs`: reusable in-memory cache for shared runtime resources
- `dream/HudLayout.cs`: viewport-driven HUD crosshair layout math
- `dream/BulletSharpExtensions.cs`: Bullet-to-OpenTK matrix conversion helper
- `dream/CubeTransforms.cs`: shared centered visual-transform and collision-debug transform helpers
- `dream/VectorConversion.cs`: conversions between OpenTK and BulletSharp vector types
- `dream/MatrixExtensions.cs`: conversion from `System.Numerics.Matrix4x4` to `OpenTK.Matrix4`
- `dream/GLUtils.cs`: OpenGL error helper

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
- `players/`: Kenney blocky player model, texture dependency, and copied CC0 license for remote-player rendering

### `dream/fonts/`

- `Arial.ttf`

## Documentation Files

- `README.md`: quickstart and high-level entry point
- `docs/overview.md`: high-level project summary
- `docs/architecture.md`: subsystem interaction guide
- `docs/project-structure.md`: repository and file guide
- `docs/configuration.md`: framework, packages, build, and runtime assumptions
- `docs/rendering-and-assets.md`: rendering path and asset usage details
