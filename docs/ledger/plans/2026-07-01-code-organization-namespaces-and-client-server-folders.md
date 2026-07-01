# Code Organization Namespaces And Client Server Folders Implementation Plan

Status: completed
Note: this document is retained as implementation history. The live folder and namespace map is documented in `docs/project-structure.md` and `docs/architecture.md`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the flat `dream/` source layout with folder and namespace boundaries that make the client, server, protocol, simulation, rendering, and shared domain seams obvious.

**Architecture:** Do this as a staged single-project migration first: add namespace/folder conventions, move files by bounded context, keep tests green after each slice, and update docs. Only after the code compiles cleanly behind namespaces should we consider splitting into multiple `.csproj` assemblies such as `Dream.Client`, `Dream.Server`, and shared libraries.

**Tech Stack:** C# / .NET 10, file-scoped namespaces, existing `dream.csproj`, xUnit, OpenTK, BulletSharp, ImGui.NET, SharpGLTF.

---

## Current State

This section records the pre-migration state that motivated the work.

The production project currently has `237` C# source files directly under `dream/`. The only nested production folders are asset/build folders such as `animations/`, `fonts/`, `shaders/`, `bin/`, and `obj/`.

This worked while the project was small, but it now hides important boundaries:

- desktop client shell and scenes
- startup connection UI
- remote client transport
- dedicated server runtime
- shared protocol contracts
- authoritative simulation/runtime
- rendering and asset loading
- combat/player/enemy/world domain logic
- infrastructure utilities

The test project also has a flat `dream.Tests/` layout with `91` C# files. Tests should be reorganized after production folders settle so they mirror the new namespaces.

## Target Namespace And Folder Map

Use `Dream` as the root namespace. Prefer file-scoped namespaces:

```csharp
namespace Dream.Multiplayer.Protocol;
```

Target production folders:

- `dream/Application/`: process startup, window shell, settings, app-level runtime state.
- `dream/Client/Startup/`: connection screen state, coordinator, integrated local-host boot path.
- `dream/Client/Scenes/`: gameplay scene composition and scene-local systems.
- `dream/Client/Input/`: command capture, input resume filters, one-shot input filters.
- `dream/Client/Networking/`: remote client session, connector, client transport adapters.
- `dream/Client/Replication/`: client-side application of authoritative snapshots/actions.
- `dream/Client/Presentation/`: client scene presenter, interpolation, remote/local presentation DTOs.
- `dream/Server/`: server bootstrap, dedicated server host/service, server connection loop.
- `dream/Multiplayer/Protocol/`: message contracts, action payloads, result payloads, codec, framer, router.
- `dream/Multiplayer/Transport/`: TCP transport and transport abstractions.
- `dream/Multiplayer/Sessions/`: player session identity, registry, connected-player records, command inboxes.
- `dream/Multiplayer/Diagnostics/`: authoritative flow traces and diagnostics sinks.
- `dream/Simulation/Runtime/`: `WorldSessionRuntime`, presentation/snapshot export seams, authoritative targets.
- `dream/Simulation/World/`: map, grid, world snapshots/mutations, world object records, visibility.
- `dream/Simulation/Players/`: player, movement, camera rig, combatant, stats, interaction, command intents.
- `dream/Simulation/Enemies/`: enemy actors, registry, wolf policies, enemy collision/movement.
- `dream/Simulation/Combat/`: health, damage, combat actors, combat use resolution, floating-text event facts.
- `dream/Simulation/Physics/`: Bullet integration, physics factory, sync, transforms, conversion helpers.
- `dream/Rendering/Core/`: `Render`, shader, OpenGL helpers, model cache, low-level rendering helpers.
- `dream/Rendering/Models/`: animated model, skinning/model transform helpers, model asset definitions.
- `dream/Rendering/World/`: world renderer, batch planner, cube transforms, debug rendering.
- `dream/Rendering/Actors/`: enemy renderer/state, local/remote player renderer/state, world actor labels.
- `dream/Rendering/Hud/`: HUD renderer/layout/status, floating combat text rendering styles/tuning.
- `dream/Rendering/ViewModels/`: first-person viewmodel assets, catalog, pose resolver, renderer, rig config.
- `dream/Assets/`: asset path helpers and runtime asset definitions.
- `dream/Configuration/`: text config load/save models.
- `dream/Utilities/`: small generic helpers that do not belong to a richer subsystem.

Target test folders should mirror production enough to aid discovery:

- `dream.Tests/Application/`
- `dream.Tests/Client/`
- `dream.Tests/Server/`
- `dream.Tests/Multiplayer/`
- `dream.Tests/Simulation/`
- `dream.Tests/Rendering/`
- `dream.Tests/Assets/`
- `dream.Tests/Configuration/`
- `dream.Tests/Utilities/`

## Namespace Rules

- Use file-scoped namespaces everywhere production code is moved.
- Namespace must match the folder path under the `Dream` root, for example `dream/Client/Networking/RemoteClientSession.cs` uses `namespace Dream.Client.Networking;`.
- Test namespaces should mirror production with `.Tests`, for example `Dream.Tests.Client.Networking`.
- Keep `using` directives explicit at the top of each file.
- Do not create broad global aliases for ambiguous math types. Files that touch both BulletSharp and OpenTK should keep local aliases such as `using Vector3 = OpenTK.Mathematics.Vector3;`.
- Do not create a giant `GlobalUsings.cs` just to hide dependencies. If a dependency is meaningful to a subsystem, keep the `using` visible in that file.
- Shared protocol DTOs must not depend on OpenGL, ImGui, OpenTK windowing, or client rendering types.
- Server namespaces must not depend on `Dream.Rendering`, `Dream.Client.Startup`, ImGui, or OpenTK windowing types.
- Client namespaces may depend on shared protocol, rendering, and simulation presentation seams, but should not own authoritative simulation rules directly.

## Project Split Strategy

Phase 1 keeps a single `dream.csproj`. This is deliberate.

Folders and namespaces are a low-risk first step. A multi-project split is valuable later, but only after dependency direction is visible. Splitting too early would force asset copying, project references, nullable/implicit using policies, and executable packaging changes while the code still has cross-boundary references.

Later candidate projects:

- `Dream.Shared`: protocol DTOs, session identity, low-level shared contracts.
- `Dream.Simulation`: authoritative world/player/enemy/combat runtime.
- `Dream.Rendering`: OpenGL/SharpGLTF rendering infrastructure.
- `Dream.Client`: desktop shell, startup UI, PlayScene, client networking, presentation.
- `Dream.Server`: headless server bootstrap, dedicated service loop, transport host.
- `Dream.Tests`: test project referencing the needed assemblies.

The split is a follow-up decision, not part of the first folder migration.

---

## Task 1: Add Structure Guardrails

**Files:**
- Create: `dream.Tests/ProjectStructure/SourceLayoutTests.cs`
- Modify: `docs/project-structure.md`

- [ ] **Step 1: Write a failing structure test that exposes the flat layout**

Create `dream.Tests/ProjectStructure/SourceLayoutTests.cs`:

```csharp
using System.IO;
using Xunit;

namespace Dream.Tests.ProjectStructure;

public class SourceLayoutTests
{
    [Fact]
    public void ProductionSourceRoot_DoesNotRemainFlatAfterOrganizationPass()
    {
        string repoRoot = FindRepoRoot();
        string productionRoot = Path.Combine(repoRoot, "dream");

        string[] rootSourceFiles = Directory
            .GetFiles(productionRoot, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not "Program.cs")
            .ToArray()!;

        Assert.True(
            rootSourceFiles.Length <= 3,
            $"Only entry-point or compatibility files should remain at dream/ root. Found: {string.Join(", ", rootSourceFiles)}");
    }

    [Fact]
    public void ProductionSourceRoot_HasTopLevelBoundedContextFolders()
    {
        string repoRoot = FindRepoRoot();
        string productionRoot = Path.Combine(repoRoot, "dream");
        string[] expectedFolders =
        [
            "Application",
            "Client",
            "Server",
            "Multiplayer",
            "Simulation",
            "Rendering",
            "Assets",
            "Configuration",
            "Utilities"
        ];

        foreach (string folder in expectedFolders)
        {
            Assert.True(
                Directory.Exists(Path.Combine(productionRoot, folder)),
                $"Missing source folder: dream/{folder}");
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
```

- [ ] **Step 2: Run the structure tests and verify red**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~SourceLayoutTests"
```

Expected: fail because `dream/` still contains many root-level C# files and the target folders do not exist.

- [ ] **Step 3: Add the target top-level folders**

Run:

```powershell
New-Item -ItemType Directory -Force `
  dream\Application, `
  dream\Client\Startup, `
  dream\Client\Scenes, `
  dream\Client\Input, `
  dream\Client\Networking, `
  dream\Client\Replication, `
  dream\Client\Presentation, `
  dream\Server, `
  dream\Multiplayer\Protocol, `
  dream\Multiplayer\Transport, `
  dream\Multiplayer\Sessions, `
  dream\Multiplayer\Diagnostics, `
  dream\Simulation\Runtime, `
  dream\Simulation\World, `
  dream\Simulation\Players, `
  dream\Simulation\Enemies, `
  dream\Simulation\Combat, `
  dream\Simulation\Physics, `
  dream\Rendering\Core, `
  dream\Rendering\Models, `
  dream\Rendering\World, `
  dream\Rendering\Actors, `
  dream\Rendering\Hud, `
  dream\Rendering\ViewModels, `
  dream\Assets, `
  dream\Configuration, `
  dream\Utilities, `
  dream.Tests\ProjectStructure
```

- [ ] **Step 4: Keep tests red until file moves are done**

Run the same `SourceLayoutTests` command.

Expected: the folder test passes, the flat-root file count test still fails.

---

## Task 2: Set Root Namespace Intent

**Files:**
- Modify: `dream/dream.csproj`
- Modify: `dream.Tests/dream.Tests.csproj`
- Create: `dream.Tests/ProjectStructure/NamespaceConventionTests.cs`

- [ ] **Step 1: Add explicit project root namespaces**

Modify `dream/dream.csproj`:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <RootNamespace>Dream</RootNamespace>
  <AssemblyName>Dream</AssemblyName>
</PropertyGroup>
```

Modify `dream.Tests/dream.Tests.csproj`:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <IsPackable>false</IsPackable>
  <RootNamespace>Dream.Tests</RootNamespace>
</PropertyGroup>
```

- [ ] **Step 2: Write namespace convention tests**

Create `dream.Tests/ProjectStructure/NamespaceConventionTests.cs`:

```csharp
using System.IO;
using Xunit;

namespace Dream.Tests.ProjectStructure;

public class NamespaceConventionTests
{
    [Fact]
    public void MovedProductionFiles_UseDreamFileScopedNamespace()
    {
        string repoRoot = FindRepoRoot();
        string productionRoot = Path.Combine(repoRoot, "dream");
        string[] sourceFolders =
        [
            "Application",
            "Client",
            "Server",
            "Multiplayer",
            "Simulation",
            "Rendering",
            "Assets",
            "Configuration",
            "Utilities"
        ];

        foreach (string folder in sourceFolders)
        {
            foreach (string file in Directory.GetFiles(Path.Combine(productionRoot, folder), "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(productionRoot, file);
                string expectedNamespace = "namespace Dream." +
                    Path.GetDirectoryName(relative)!.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.') +
                    ";";
                string content = File.ReadAllText(file);

                Assert.Contains(expectedNamespace, content);
            }
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
```

- [ ] **Step 3: Run namespace convention tests**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~NamespaceConventionTests"
```

Expected: pass while folders are empty, then protect each migration slice as files move.

---

## Task 3: Move Application And Startup Shell Files

**Files:**
- Move: `dream/Program.cs` to `dream/Application/Program.cs`
- Move: `dream/Game.cs` to `dream/Application/Game.cs`
- Move: `dream/GameSettings.cs` to `dream/Application/GameSettings.cs`
- Move: `dream/RuntimeState.cs` to `dream/Application/RuntimeState.cs`
- Move: `dream/GameEvents.cs` to `dream/Application/GameEvents.cs`
- Move: `dream/GameWindowTitleBuilder.cs` to `dream/Application/GameWindowTitleBuilder.cs`
- Move: `dream/CameraViewMode.cs` to `dream/Application/CameraViewMode.cs`
- Move: `dream/DesktopClientConfig.cs` to `dream/Configuration/DesktopClientConfig.cs`
- Move: `dream/DesktopClientTextConfig.cs` to `dream/Configuration/DesktopClientTextConfig.cs`
- Move: `dream/ViewModelRigTextConfig.cs` to `dream/Configuration/ViewModelRigTextConfig.cs`
- Move matching tests to `dream.Tests/Application/` and `dream.Tests/Configuration/`

- [ ] **Step 1: Move files with git**

Run:

```powershell
git mv dream\Program.cs dream\Application\Program.cs
git mv dream\Game.cs dream\Application\Game.cs
git mv dream\GameSettings.cs dream\Application\GameSettings.cs
git mv dream\RuntimeState.cs dream\Application\RuntimeState.cs
git mv dream\GameEvents.cs dream\Application\GameEvents.cs
git mv dream\GameWindowTitleBuilder.cs dream\Application\GameWindowTitleBuilder.cs
git mv dream\CameraViewMode.cs dream\Application\CameraViewMode.cs
git mv dream\DesktopClientConfig.cs dream\Configuration\DesktopClientConfig.cs
git mv dream\DesktopClientTextConfig.cs dream\Configuration\DesktopClientTextConfig.cs
git mv dream\ViewModelRigTextConfig.cs dream\Configuration\ViewModelRigTextConfig.cs
git mv dream.Tests\GameSettingsTests.cs dream.Tests\Application\GameSettingsTests.cs
git mv dream.Tests\GameWindowTitleBuilderTests.cs dream.Tests\Application\GameWindowTitleBuilderTests.cs
git mv dream.Tests\DesktopClientTextConfigTests.cs dream.Tests\Configuration\DesktopClientTextConfigTests.cs
git mv dream.Tests\ViewModelRigTextConfigTests.cs dream.Tests\Configuration\ViewModelRigTextConfigTests.cs
```

- [ ] **Step 2: Add file-scoped namespaces**

Use these namespaces:

```csharp
namespace Dream.Application;
namespace Dream.Configuration;
namespace Dream.Tests.Application;
namespace Dream.Tests.Configuration;
```

Add explicit `using` directives where compile errors point to moved types, such as:

```csharp
using Dream.Client.Startup;
using Dream.Client.Scenes;
using Dream.Configuration;
using Dream.Server;
```

- [ ] **Step 3: Build and fix only namespace/using errors**

Run:

```powershell
dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false
```

Expected: success or compile errors only for missing namespaces/usings. Fix those errors without changing behavior.

- [ ] **Step 4: Run focused tests**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~GameSettingsTests|FullyQualifiedName~GameWindowTitleBuilderTests|FullyQualifiedName~DesktopClientTextConfigTests|FullyQualifiedName~ViewModelRigTextConfigTests"
```

Expected: pass.

---

## Task 4: Move Client Shell, Input, Networking, Replication, And Presentation

**Files:**
- Move startup UI/lifecycle files into `dream/Client/Startup/`
- Move `PlayScene.cs` into `dream/Client/Scenes/`
- Move input filters/controllers into `dream/Client/Input/`
- Move remote client connection files into `dream/Client/Networking/`
- Move client authoritative intake files into `dream/Client/Replication/`
- Move client presenter/interpolation DTOs into `dream/Client/Presentation/`
- Move matching tests under `dream.Tests/Client/`

- [ ] **Step 1: Move startup and scene files**

Run:

```powershell
git mv dream\StartupConnectionScene.cs dream\Client\Startup\StartupConnectionScene.cs
git mv dream\StartupMenuRenderer.cs dream\Client\Startup\StartupMenuRenderer.cs
git mv dream\NetworkSessionCoordinator.cs dream\Client\Startup\NetworkSessionCoordinator.cs
git mv dream\ConnectionFlowState.cs dream\Client\Startup\ConnectionFlowState.cs
git mv dream\ConnectionUiRequest.cs dream\Client\Startup\ConnectionUiRequest.cs
git mv dream\ClientConnectionOptions.cs dream\Client\Startup\ClientConnectionOptions.cs
git mv dream\IntegratedLocalServerHandle.cs dream\Client\Startup\IntegratedLocalServerHandle.cs
git mv dream\ILocalServerHandle.cs dream\Client\Startup\ILocalServerHandle.cs
git mv dream\PlayScene.cs dream\Client\Scenes\PlayScene.cs
```

- [ ] **Step 2: Move input files**

Run:

```powershell
git mv dream\PlayerController.cs dream\Client\Input\PlayerController.cs
git mv dream\GameplayInputResumeFilter.cs dream\Client\Input\GameplayInputResumeFilter.cs
git mv dream\PlayerCommandOneShotInputFilter.cs dream\Client\Input\PlayerCommandOneShotInputFilter.cs
git mv dream\ImGuiTextInputQueue.cs dream\Client\Input\ImGuiTextInputQueue.cs
```

- [ ] **Step 3: Move client networking files**

Run:

```powershell
git mv dream\RemoteClientSession.cs dream\Client\Networking\RemoteClientSession.cs
git mv dream\RemoteClientOptions.cs dream\Client\Networking\RemoteClientOptions.cs
git mv dream\RemotePlayerCommandRelay.cs dream\Client\Networking\RemotePlayerCommandRelay.cs
git mv dream\RemoteSessionConnector.cs dream\Client\Networking\RemoteSessionConnector.cs
git mv dream\IRemoteSessionConnector.cs dream\Client\Networking\IRemoteSessionConnector.cs
git mv dream\TcpClientConnector.cs dream\Client\Networking\TcpClientConnector.cs
git mv dream\IClientTransport.cs dream\Client\Networking\IClientTransport.cs
```

- [ ] **Step 4: Move client replication files**

Run:

```powershell
git mv dream\ClientAuthoritativeActionApplier.cs dream\Client\Replication\ClientAuthoritativeActionApplier.cs
git mv dream\ClientCorrectionApplier.cs dream\Client\Replication\ClientCorrectionApplier.cs
git mv dream\ClientCorrectionDelta.cs dream\Client\Replication\ClientCorrectionDelta.cs
git mv dream\ClientJoinAcceptedApplier.cs dream\Client\Replication\ClientJoinAcceptedApplier.cs
git mv dream\ClientSnapshotDamageApplier.cs dream\Client\Replication\ClientSnapshotDamageApplier.cs
git mv dream\ClientSimulationCommandPolicy.cs dream\Client\Replication\ClientSimulationCommandPolicy.cs
git mv dream\RemoteEnemyStateApplier.cs dream\Client\Replication\RemoteEnemyStateApplier.cs
git mv dream\RemoteWorldMutationApplier.cs dream\Client\Replication\RemoteWorldMutationApplier.cs
git mv dream\IReplicatedPlayerTarget.cs dream\Client\Replication\IReplicatedPlayerTarget.cs
git mv dream\IReplicatedSessionStateTarget.cs dream\Client\Replication\IReplicatedSessionStateTarget.cs
```

- [ ] **Step 5: Move client presentation files**

Run:

```powershell
git mv dream\ClientScenePresenter.cs dream\Client\Presentation\ClientScenePresenter.cs
git mv dream\RemotePlayerPresentationState.cs dream\Client\Presentation\RemotePlayerPresentationState.cs
git mv dream\RemotePlayerInterpolationBuffer.cs dream\Client\Presentation\RemotePlayerInterpolationBuffer.cs
git mv dream\RemotePlayerSnapshotSample.cs dream\Client\Presentation\RemotePlayerSnapshotSample.cs
git mv dream\InterpolatedActorPose.cs dream\Client\Presentation\InterpolatedActorPose.cs
git mv dream\LocalPlayerPresentationState.cs dream\Client\Presentation\LocalPlayerPresentationState.cs
git mv dream\ThirdPersonCameraRig.cs dream\Client\Presentation\ThirdPersonCameraRig.cs
```

- [ ] **Step 6: Apply namespaces and explicit usings**

Use:

```csharp
namespace Dream.Client.Startup;
namespace Dream.Client.Scenes;
namespace Dream.Client.Input;
namespace Dream.Client.Networking;
namespace Dream.Client.Replication;
namespace Dream.Client.Presentation;
```

Common explicit usings that will likely be needed:

```csharp
using Dream.Application;
using Dream.Client.Input;
using Dream.Client.Networking;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Configuration;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Core;
using Dream.Rendering.ViewModels;
using Dream.Simulation.Runtime;
```

- [ ] **Step 7: Move matching tests**

Run:

```powershell
New-Item -ItemType Directory -Force dream.Tests\Client\Startup, dream.Tests\Client\Input, dream.Tests\Client\Networking, dream.Tests\Client\Replication, dream.Tests\Client\Presentation
git mv dream.Tests\ClientConnectionOptionsTests.cs dream.Tests\Client\Startup\ClientConnectionOptionsTests.cs
git mv dream.Tests\NetworkSessionCoordinatorTests.cs dream.Tests\Client\Startup\NetworkSessionCoordinatorTests.cs
git mv dream.Tests\GameplayInputResumeFilterTests.cs dream.Tests\Client\Input\GameplayInputResumeFilterTests.cs
git mv dream.Tests\ImGuiTextInputQueueTests.cs dream.Tests\Client\Input\ImGuiTextInputQueueTests.cs
git mv dream.Tests\PlayerCommandOneShotInputFilterTests.cs dream.Tests\Client\Input\PlayerCommandOneShotInputFilterTests.cs
git mv dream.Tests\PlayerControllerTests.cs dream.Tests\Client\Input\PlayerControllerTests.cs
git mv dream.Tests\RemoteClientSessionTests.cs dream.Tests\Client\Networking\RemoteClientSessionTests.cs
git mv dream.Tests\ClientAuthoritativeStateApplierTests.cs dream.Tests\Client\Replication\ClientAuthoritativeStateApplierTests.cs
git mv dream.Tests\ClientSimulationCommandPolicyTests.cs dream.Tests\Client\Replication\ClientSimulationCommandPolicyTests.cs
git mv dream.Tests\RemoteEnemyStateApplierTests.cs dream.Tests\Client\Replication\RemoteEnemyStateApplierTests.cs
git mv dream.Tests\ClientScenePresenterTests.cs dream.Tests\Client\Presentation\ClientScenePresenterTests.cs
```

- [ ] **Step 8: Run focused client tests**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~Dream.Tests.Client"
```

Expected: pass.

---

## Task 5: Move Server, Transport, Protocol, Sessions, And Diagnostics

**Files:**
- Move dedicated server files to `dream/Server/`
- Move shared protocol files to `dream/Multiplayer/Protocol/`
- Move transport files to `dream/Multiplayer/Transport/`
- Move session files to `dream/Multiplayer/Sessions/`
- Move diagnostics files to `dream/Multiplayer/Diagnostics/`
- Move matching tests under `dream.Tests/Server/` and `dream.Tests/Multiplayer/`

- [ ] **Step 1: Move server host files**

Run:

```powershell
git mv dream\DedicatedServerHost.cs dream\Server\DedicatedServerHost.cs
git mv dream\DedicatedServerService.cs dream\Server\DedicatedServerService.cs
git mv dream\ServerBootstrap.cs dream\Server\ServerBootstrap.cs
git mv dream\ServerConnection.cs dream\Server\ServerConnection.cs
git mv dream\ITickDrivenAuthoritativeSessionHost.cs dream\Server\ITickDrivenAuthoritativeSessionHost.cs
```

- [ ] **Step 2: Move protocol contracts and routing**

Run:

```powershell
git mv dream\IProtocolMessage.cs dream\Multiplayer\Protocol\IProtocolMessage.cs
git mv dream\IClientToServerMessage.cs dream\Multiplayer\Protocol\IClientToServerMessage.cs
git mv dream\IServerToClientMessage.cs dream\Multiplayer\Protocol\IServerToClientMessage.cs
git mv dream\ClientHelloMessage.cs dream\Multiplayer\Protocol\ClientHelloMessage.cs
git mv dream\ClientJoinRequestMessage.cs dream\Multiplayer\Protocol\ClientJoinRequestMessage.cs
git mv dream\ServerHelloMessage.cs dream\Multiplayer\Protocol\ServerHelloMessage.cs
git mv dream\JoinAcceptedMessage.cs dream\Multiplayer\Protocol\JoinAcceptedMessage.cs
git mv dream\JoinRejectedMessage.cs dream\Multiplayer\Protocol\JoinRejectedMessage.cs
git mv dream\ServerSnapshotMessage.cs dream\Multiplayer\Protocol\ServerSnapshotMessage.cs
git mv dream\PlayerCommandMessage.cs dream\Multiplayer\Protocol\PlayerCommandMessage.cs
git mv dream\PlayerActionMessage.cs dream\Multiplayer\Protocol\PlayerActionMessage.cs
git mv dream\ServerActionMessage.cs dream\Multiplayer\Protocol\ServerActionMessage.cs
git mv dream\IPlayerActionPayload.cs dream\Multiplayer\Protocol\IPlayerActionPayload.cs
git mv dream\IActionResultPayload.cs dream\Multiplayer\Protocol\IActionResultPayload.cs
git mv dream\BuildUseIntent.cs dream\Multiplayer\Protocol\BuildUseIntent.cs
git mv dream\CombatUseIntent.cs dream\Multiplayer\Protocol\CombatUseIntent.cs
git mv dream\PlayerStatsUpdateIntent.cs dream\Multiplayer\Protocol\PlayerStatsUpdateIntent.cs
git mv dream\AuthoritativeActionResult.cs dream\Multiplayer\Protocol\AuthoritativeActionResult.cs
git mv dream\WorldMutationResult.cs dream\Multiplayer\Protocol\WorldMutationResult.cs
git mv dream\CombatResolutionResult.cs dream\Multiplayer\Protocol\CombatResolutionResult.cs
git mv dream\PlayerStatsUpdateResult.cs dream\Multiplayer\Protocol\PlayerStatsUpdateResult.cs
git mv dream\RejectedUseDelta.cs dream\Multiplayer\Protocol\RejectedUseDelta.cs
git mv dream\TransportEnvelope.cs dream\Multiplayer\Protocol\TransportEnvelope.cs
git mv dream\DecodedProtocolMessage.cs dream\Multiplayer\Protocol\DecodedProtocolMessage.cs
git mv dream\IProtocolCodec.cs dream\Multiplayer\Protocol\IProtocolCodec.cs
git mv dream\TransportJsonCodec.cs dream\Multiplayer\Protocol\TransportJsonCodec.cs
git mv dream\Vector3JsonConverter.cs dream\Multiplayer\Protocol\Vector3JsonConverter.cs
git mv dream\IMessageFramer.cs dream\Multiplayer\Protocol\IMessageFramer.cs
git mv dream\LengthPrefixedMessageFramer.cs dream\Multiplayer\Protocol\LengthPrefixedMessageFramer.cs
git mv dream\IProtocolRouter.cs dream\Multiplayer\Protocol\IProtocolRouter.cs
git mv dream\ProtocolRouter.cs dream\Multiplayer\Protocol\ProtocolRouter.cs
git mv dream\IClientMessageHandler.cs dream\Multiplayer\Protocol\IClientMessageHandler.cs
git mv dream\IServerMessageHandler.cs dream\Multiplayer\Protocol\IServerMessageHandler.cs
git mv dream\ClientProtocolSession.cs dream\Multiplayer\Protocol\ClientProtocolSession.cs
git mv dream\ClientRoutingContext.cs dream\Multiplayer\Protocol\ClientRoutingContext.cs
git mv dream\ServerRoutingContext.cs dream\Multiplayer\Protocol\ServerRoutingContext.cs
git mv dream\ClientHelloHandler.cs dream\Multiplayer\Protocol\ClientHelloHandler.cs
git mv dream\ServerHelloMessageHandler.cs dream\Multiplayer\Protocol\ServerHelloMessageHandler.cs
git mv dream\JoinRequestHandler.cs dream\Multiplayer\Protocol\JoinRequestHandler.cs
git mv dream\JoinAcceptedMessageHandler.cs dream\Multiplayer\Protocol\JoinAcceptedMessageHandler.cs
git mv dream\JoinRejectedMessageHandler.cs dream\Multiplayer\Protocol\JoinRejectedMessageHandler.cs
git mv dream\PlayerCommandHandler.cs dream\Multiplayer\Protocol\PlayerCommandHandler.cs
git mv dream\PlayerActionHandler.cs dream\Multiplayer\Protocol\PlayerActionHandler.cs
git mv dream\ServerSnapshotMessageHandler.cs dream\Multiplayer\Protocol\ServerSnapshotMessageHandler.cs
git mv dream\ServerActionMessageHandler.cs dream\Multiplayer\Protocol\ServerActionMessageHandler.cs
```

- [ ] **Step 3: Move transport files**

Run:

```powershell
git mv dream\IServerTransport.cs dream\Multiplayer\Transport\IServerTransport.cs
git mv dream\ITransportConnection.cs dream\Multiplayer\Transport\ITransportConnection.cs
git mv dream\TcpServerTransport.cs dream\Multiplayer\Transport\TcpServerTransport.cs
git mv dream\TcpClientTransport.cs dream\Multiplayer\Transport\TcpClientTransport.cs
```

- [ ] **Step 4: Move sessions and diagnostics**

Run:

```powershell
git mv dream\ConnectedPlayer.cs dream\Multiplayer\Sessions\ConnectedPlayer.cs
git mv dream\PlayerRosterEntry.cs dream\Multiplayer\Sessions\PlayerRosterEntry.cs
git mv dream\PlayerSessionId.cs dream\Multiplayer\Sessions\PlayerSessionId.cs
git mv dream\PlayerSessionRegistry.cs dream\Multiplayer\Sessions\PlayerSessionRegistry.cs
git mv dream\IPlayerCommandInbox.cs dream\Multiplayer\Sessions\IPlayerCommandInbox.cs
git mv dream\LatestPlayerCommandInbox.cs dream\Multiplayer\Sessions\LatestPlayerCommandInbox.cs
git mv dream\AuthoritativeFlowTrace.cs dream\Multiplayer\Diagnostics\AuthoritativeFlowTrace.cs
git mv dream\AuthoritativeFlowTraceEntry.cs dream\Multiplayer\Diagnostics\AuthoritativeFlowTraceEntry.cs
git mv dream\EventTraceSystem.cs dream\Multiplayer\Diagnostics\EventTraceSystem.cs
git mv dream\IMultiplayerDiagnosticsSink.cs dream\Multiplayer\Diagnostics\IMultiplayerDiagnosticsSink.cs
git mv dream\MultiplayerDiagnostics.cs dream\Multiplayer\Diagnostics\MultiplayerDiagnostics.cs
```

- [ ] **Step 5: Apply namespaces and run focused tests**

Use:

```csharp
namespace Dream.Server;
namespace Dream.Multiplayer.Protocol;
namespace Dream.Multiplayer.Transport;
namespace Dream.Multiplayer.Sessions;
namespace Dream.Multiplayer.Diagnostics;
```

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~Protocol|FullyQualifiedName~Server|FullyQualifiedName~DedicatedServer|FullyQualifiedName~TcpServer|FullyQualifiedName~RemoteClientSession|FullyQualifiedName~PlayerSessionRegistry|FullyQualifiedName~LatestPlayerCommandInbox|FullyQualifiedName~MultiplayerDiagnostics"
```

Expected: pass.

---

## Task 6: Move Simulation Runtime, World, Player, Enemy, Combat, And Physics

**Files:**
- Move runtime files into `dream/Simulation/Runtime/`
- Move world files into `dream/Simulation/World/`
- Move player files into `dream/Simulation/Players/`
- Move enemy files into `dream/Simulation/Enemies/`
- Move combat files into `dream/Simulation/Combat/`
- Move physics files into `dream/Simulation/Physics/`
- Move matching tests under `dream.Tests/Simulation/`

- [ ] **Step 1: Move runtime files**

Run:

```powershell
git mv dream\WorldSessionRuntime.cs dream\Simulation\Runtime\WorldSessionRuntime.cs
git mv dream\WorldSessionPresentationState.cs dream\Simulation\Runtime\WorldSessionPresentationState.cs
git mv dream\RuntimeAuthoritativeSessionHost.cs dream\Simulation\Runtime\RuntimeAuthoritativeSessionHost.cs
git mv dream\AuthoritativePlayerTarget.cs dream\Simulation\Runtime\AuthoritativePlayerTarget.cs
git mv dream\AuthoritativeSessionActionContext.cs dream\Simulation\Runtime\AuthoritativeSessionActionContext.cs
git mv dream\IAuthoritativePlayerTarget.cs dream\Simulation\Runtime\IAuthoritativePlayerTarget.cs
git mv dream\IAuthoritativeSessionHost.cs dream\Simulation\Runtime\IAuthoritativeSessionHost.cs
git mv dream\RemoteSessionPlayerState.cs dream\Simulation\Runtime\RemoteSessionPlayerState.cs
git mv dream\ServerTickRunner.cs dream\Simulation\Runtime\ServerTickRunner.cs
git mv dream\SimulationClock.cs dream\Simulation\Runtime\SimulationClock.cs
```

- [ ] **Step 2: Move world files**

Run:

```powershell
git mv dream\Map.cs dream\Simulation\World\Map.cs
git mv dream\WorldGrid.cs dream\Simulation\World\WorldGrid.cs
git mv dream\WorldGenerator.cs dream\Simulation\World\WorldGenerator.cs
git mv dream\WorldPhysicsBinder.cs dream\Simulation\World\WorldPhysicsBinder.cs
git mv dream\WorldSpatialIndex.cs dream\Simulation\World\WorldSpatialIndex.cs
git mv dream\WorldVisibility.cs dream\Simulation\World\WorldVisibility.cs
git mv dream\DestructibleCubeRegistry.cs dream\Simulation\World\WorldObjectRegistry.cs
git mv dream\Cube.cs dream\Simulation\World\Cube.cs
git mv dream\CubeDestroyedEvent.cs dream\Simulation\World\CubeDestroyedEvent.cs
git mv dream\CubePlacedEvent.cs dream\Simulation\World\CubePlacedEvent.cs
git mv dream\ActorSnapshot.cs dream\Simulation\World\ActorSnapshot.cs
git mv dream\ServerSnapshotEnvelope.cs dream\Simulation\World\ServerSnapshotEnvelope.cs
git mv dream\WorldSnapshot.cs dream\Simulation\World\WorldSnapshot.cs
git mv dream\WorldMutationDelta.cs dream\Simulation\World\WorldMutationDelta.cs
git mv dream\DamageEventDelta.cs dream\Simulation\World\DamageEventDelta.cs
```

- [ ] **Step 3: Move player files**

Run:

```powershell
git mv dream\Player.cs dream\Simulation\Players\Player.cs
git mv dream\Camera.cs dream\Simulation\Players\Camera.cs
git mv dream\PlayerCameraRig.cs dream\Simulation\Players\PlayerCameraRig.cs
git mv dream\PlayerMotor.cs dream\Simulation\Players\PlayerMotor.cs
git mv dream\PlayerInteraction.cs dream\Simulation\Players\PlayerInteraction.cs
git mv dream\PlayerCommands.cs dream\Simulation\Players\PlayerCommands.cs
git mv dream\PlayerCommandFrame.cs dream\Simulation\Players\PlayerCommandFrame.cs
git mv dream\PlayerMoveIntent.cs dream\Simulation\Players\PlayerMoveIntent.cs
git mv dream\PlayerLookIntent.cs dream\Simulation\Players\PlayerLookIntent.cs
git mv dream\PlayerModeSwitchIntent.cs dream\Simulation\Players\PlayerModeSwitchIntent.cs
git mv dream\PlayerInteractionMode.cs dream\Simulation\Players\PlayerInteractionMode.cs
git mv dream\PlayerMovementResolver.cs dream\Simulation\Players\PlayerMovementResolver.cs
git mv dream\PlayerCombatant.cs dream\Simulation\Players\PlayerCombatant.cs
git mv dream\PlayerCombatStats.cs dream\Simulation\Players\PlayerCombatStats.cs
git mv dream\PlayerCombatDerivedStats.cs dream\Simulation\Players\PlayerCombatDerivedStats.cs
git mv dream\PlayerCombatStatsSnapshot.cs dream\Simulation\Players\PlayerCombatStatsSnapshot.cs
git mv dream\PlayerAttackResolution.cs dream\Simulation\Players\PlayerAttackResolution.cs
```

- [ ] **Step 4: Move enemy, combat, and physics files**

Run:

```powershell
git mv dream\EnemyActor.cs dream\Simulation\Enemies\EnemyActor.cs
git mv dream\EnemyBehaviorState.cs dream\Simulation\Enemies\EnemyBehaviorState.cs
git mv dream\EnemyCombatant.cs dream\Simulation\Enemies\EnemyCombatant.cs
git mv dream\EnemyMovementCollision.cs dream\Simulation\Enemies\EnemyMovementCollision.cs
git mv dream\EnemyPlayerCollisionResolver.cs dream\Simulation\Enemies\EnemyPlayerCollisionResolver.cs
git mv dream\EnemyRegistry.cs dream\Simulation\Enemies\EnemyRegistry.cs
git mv dream\WolfSpawnPolicy.cs dream\Simulation\Enemies\WolfSpawnPolicy.cs
git mv dream\CombatActorHandle.cs dream\Simulation\Combat\CombatActorHandle.cs
git mv dream\CombatDemoTargetFactory.cs dream\Simulation\Combat\CombatDemoTargetFactory.cs
git mv dream\CombatUseResolver.cs dream\Simulation\Combat\CombatUseResolver.cs
git mv dream\CreatureKind.cs dream\Simulation\Combat\CreatureKind.cs
git mv dream\DamageAppliedEvent.cs dream\Simulation\Combat\DamageAppliedEvent.cs
git mv dream\ActorDiedEvent.cs dream\Simulation\Combat\ActorDiedEvent.cs
git mv dream\DamageReceipt.cs dream\Simulation\Combat\DamageReceipt.cs
git mv dream\HealthState.cs dream\Simulation\Combat\HealthState.cs
git mv dream\ICombatAnchorProvider.cs dream\Simulation\Combat\ICombatAnchorProvider.cs
git mv dream\IDamageable.cs dream\Simulation\Combat\IDamageable.cs
git mv dream\PhysicsFactory.cs dream\Simulation\Physics\PhysicsFactory.cs
git mv dream\PhysicsIntegrator.cs dream\Simulation\Physics\PhysicsIntegrator.cs
git mv dream\PhysicsSyncSystem.cs dream\Simulation\Physics\PhysicsSyncSystem.cs
git mv dream\BulletSharpExtensions.cs dream\Simulation\Physics\BulletSharpExtensions.cs
git mv dream\VectorConversion.cs dream\Simulation\Physics\VectorConversion.cs
```

- [ ] **Step 5: Apply namespaces and focused test pass**

Use:

```csharp
namespace Dream.Simulation.Runtime;
namespace Dream.Simulation.World;
namespace Dream.Simulation.Players;
namespace Dream.Simulation.Enemies;
namespace Dream.Simulation.Combat;
namespace Dream.Simulation.Physics;
```

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~World|FullyQualifiedName~Player|FullyQualifiedName~Enemy|FullyQualifiedName~Combat|FullyQualifiedName~Physics|FullyQualifiedName~SimulationClock|FullyQualifiedName~RuntimeAuthoritativeSessionHost"
```

Expected: pass.

---

## Task 7: Move Rendering, HUD, Model, ViewModel, Asset, And Utility Files

**Files:**
- Move rendering core into `dream/Rendering/Core/`
- Move model loading/assets into `dream/Rendering/Models/`
- Move world/actor/HUD/viewmodel rendering into matching folders
- Move asset and utility files into `dream/Assets/` and `dream/Utilities/`
- Move matching tests under `dream.Tests/Rendering/`, `dream.Tests/Assets/`, and `dream.Tests/Utilities/`

- [ ] **Step 1: Move rendering core and model files**

Run:

```powershell
git mv dream\Render.cs dream\Rendering\Core\Render.cs
git mv dream\Shader.cs dream\Rendering\Core\Shader.cs
git mv dream\GLUtils.cs dream\Rendering\Core\GLUtils.cs
git mv dream\ResourceCache.cs dream\Rendering\Core\ResourceCache.cs
git mv dream\ModelAssets.cs dream\Rendering\Core\ModelAssets.cs
git mv dream\AnimatedModel.cs dream\Rendering\Models\AnimatedModel.cs
git mv dream\AnimatedModelTransformComposer.cs dream\Rendering\Models\AnimatedModelTransformComposer.cs
git mv dream\AnimationPlaybackMode.cs dream\Rendering\Models\AnimationPlaybackMode.cs
git mv dream\AnimationPlaybackState.cs dream\Rendering\Models\AnimationPlaybackState.cs
git mv dream\GltfSkinningAnalysis.cs dream\Rendering\Models\GltfSkinningAnalysis.cs
git mv dream\SceneNodeTraversal.cs dream\Rendering\Models\SceneNodeTraversal.cs
git mv dream\WorldAssetNormalization.cs dream\Rendering\Models\WorldAssetNormalization.cs
git mv dream\WolfAssetDefinition.cs dream\Rendering\Models\WolfAssetDefinition.cs
git mv dream\PlayerModelAssetDefinition.cs dream\Rendering\Models\PlayerModelAssetDefinition.cs
```

- [ ] **Step 2: Move world and actor rendering files**

Run:

```powershell
git mv dream\WorldRenderer.cs dream\Rendering\World\WorldRenderer.cs
git mv dream\WorldRenderBatchPlanner.cs dream\Rendering\World\WorldRenderBatchPlanner.cs
git mv dream\CubeTransforms.cs dream\Rendering\World\CubeTransforms.cs
git mv dream\DebugRenderer.cs dream\Rendering\World\DebugRenderer.cs
git mv dream\ActorWorldLabelBuilder.cs dream\Rendering\Actors\ActorWorldLabelBuilder.cs
git mv dream\WorldActorLabelState.cs dream\Rendering\Actors\WorldActorLabelState.cs
git mv dream\EnemyRenderer.cs dream\Rendering\Actors\EnemyRenderer.cs
git mv dream\EnemyRenderState.cs dream\Rendering\Actors\EnemyRenderState.cs
git mv dream\EnemyVisualState.cs dream\Rendering\Actors\EnemyVisualState.cs
git mv dream\WolfAnimationResolver.cs dream\Rendering\Actors\WolfAnimationResolver.cs
git mv dream\LocalPlayerRenderer.cs dream\Rendering\Actors\LocalPlayerRenderer.cs
git mv dream\RemotePlayerRenderer.cs dream\Rendering\Actors\RemotePlayerRenderer.cs
git mv dream\PlayerRenderState.cs dream\Rendering\Actors\PlayerRenderState.cs
git mv dream\PlayerAnimationClipTracker.cs dream\Rendering\Actors\PlayerAnimationClipTracker.cs
```

- [ ] **Step 3: Move HUD and floating text files**

Run:

```powershell
git mv dream\HudRenderer.cs dream\Rendering\Hud\HudRenderer.cs
git mv dream\HudLayout.cs dream\Rendering\Hud\HudLayout.cs
git mv dream\HudInteractionRequest.cs dream\Rendering\Hud\HudInteractionRequest.cs
git mv dream\HudStatusSnapshot.cs dream\Rendering\Hud\HudStatusSnapshot.cs
git mv dream\CombatFeedbackSystem.cs dream\Rendering\Hud\CombatFeedbackSystem.cs
git mv dream\FloatingCombatTextKind.cs dream\Rendering\Hud\FloatingCombatTextKind.cs
git mv dream\FloatingCombatTextMotion.cs dream\Rendering\Hud\FloatingCombatTextMotion.cs
git mv dream\FloatingCombatTextRenderState.cs dream\Rendering\Hud\FloatingCombatTextRenderState.cs
git mv dream\FloatingCombatTextStyle.cs dream\Rendering\Hud\FloatingCombatTextStyle.cs
git mv dream\FloatingCombatTextStyles.cs dream\Rendering\Hud\FloatingCombatTextStyles.cs
git mv dream\FloatingCombatTextSystem.cs dream\Rendering\Hud\FloatingCombatTextSystem.cs
git mv dream\FloatingCombatTextTuning.cs dream\Rendering\Hud\FloatingCombatTextTuning.cs
git mv dream\AuthoritativeActionFeedbackSystem.cs dream\Rendering\Hud\AuthoritativeActionFeedbackSystem.cs
git mv dream\AuthoritativeActionRejectedEvent.cs dream\Rendering\Hud\AuthoritativeActionRejectedEvent.cs
```

- [ ] **Step 4: Move viewmodel, asset, and utility files**

Run:

```powershell
git mv dream\ViewModelAnimationRequest.cs dream\Rendering\ViewModels\ViewModelAnimationRequest.cs
git mv dream\ViewModelAssets.cs dream\Rendering\ViewModels\ViewModelAssets.cs
git mv dream\ViewModelBodyAnchorSettings.cs dream\Rendering\ViewModels\ViewModelBodyAnchorSettings.cs
git mv dream\ViewModelCatalog.cs dream\Rendering\ViewModels\ViewModelCatalog.cs
git mv dream\ViewModelDefinition.cs dream\Rendering\ViewModels\ViewModelDefinition.cs
git mv dream\ViewModelFrameState.cs dream\Rendering\ViewModels\ViewModelFrameState.cs
git mv dream\ViewModelLoadout.cs dream\Rendering\ViewModels\ViewModelLoadout.cs
git mv dream\ViewModelPoseResolver.cs dream\Rendering\ViewModels\ViewModelPoseResolver.cs
git mv dream\ViewModelRenderer.cs dream\Rendering\ViewModels\ViewModelRenderer.cs
git mv dream\ViewModelResolvedPose.cs dream\Rendering\ViewModels\ViewModelResolvedPose.cs
git mv dream\ViewModelRigSettings.cs dream\Rendering\ViewModels\ViewModelRigSettings.cs
git mv dream\ViewModelSlot.cs dream\Rendering\ViewModels\ViewModelSlot.cs
git mv dream\ViewModelSlotState.cs dream\Rendering\ViewModels\ViewModelSlotState.cs
git mv dream\ViewModelSlotTuning.cs dream\Rendering\ViewModels\ViewModelSlotTuning.cs
git mv dream\PlayerViewModelController.cs dream\Rendering\ViewModels\PlayerViewModelController.cs
git mv dream\AssetPaths.cs dream\Assets\AssetPaths.cs
git mv dream\MatrixExtensions.cs dream\Utilities\MatrixExtensions.cs
git mv dream\SafeDictionaryLookup.cs dream\Utilities\SafeDictionaryLookup.cs
```

- [ ] **Step 5: Apply namespaces and run focused tests**

Use:

```csharp
namespace Dream.Rendering.Core;
namespace Dream.Rendering.Models;
namespace Dream.Rendering.World;
namespace Dream.Rendering.Actors;
namespace Dream.Rendering.Hud;
namespace Dream.Rendering.ViewModels;
namespace Dream.Assets;
namespace Dream.Utilities;
```

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~Render|FullyQualifiedName~Model|FullyQualifiedName~Hud|FullyQualifiedName~ViewModel|FullyQualifiedName~Asset|FullyQualifiedName~Wolf|FullyQualifiedName~ResourceCache|FullyQualifiedName~SafeDictionaryLookup"
```

Expected: pass.

---

## Task 8: Reorganize Tests To Mirror Production

**Files:**
- Move remaining `dream.Tests/*.cs` files into target folders.
- Modify namespaces for moved tests.

- [ ] **Step 1: Create remaining test folders**

Run:

```powershell
New-Item -ItemType Directory -Force `
  dream.Tests\Application, `
  dream.Tests\Client, `
  dream.Tests\Server, `
  dream.Tests\Multiplayer, `
  dream.Tests\Simulation, `
  dream.Tests\Rendering, `
  dream.Tests\Assets, `
  dream.Tests\Configuration, `
  dream.Tests\Utilities
```

- [ ] **Step 2: Move tests according to the production type they primarily cover**

Apply this rule:

```text
If the test name matches a production type now under Dream.Simulation.*, move it under dream.Tests/Simulation.
If it matches Dream.Rendering.*, move it under dream.Tests/Rendering.
If it matches Dream.Multiplayer.*, move it under dream.Tests/Multiplayer.
If it matches Dream.Server.*, move it under dream.Tests/Server.
If it matches Dream.Client.*, move it under dream.Tests/Client.
```

Example moves:

```powershell
git mv dream.Tests\WorldVisibilityTests.cs dream.Tests\Simulation\World\WorldVisibilityTests.cs
git mv dream.Tests\PlayerMotorTests.cs dream.Tests\Simulation\Players\PlayerMotorTests.cs
git mv dream.Tests\EnemyActorTests.cs dream.Tests\Simulation\Enemies\EnemyActorTests.cs
git mv dream.Tests\CombatUseResolverTests.cs dream.Tests\Simulation\Combat\CombatUseResolverTests.cs
git mv dream.Tests\PhysicsIntegratorOwnershipTests.cs dream.Tests\Simulation\Physics\PhysicsIntegratorOwnershipTests.cs
git mv dream.Tests\RemotePlayerRendererTests.cs dream.Tests\Rendering\Actors\RemotePlayerRendererTests.cs
git mv dream.Tests\ProtocolFoundationTests.cs dream.Tests\Multiplayer\Protocol\ProtocolFoundationTests.cs
git mv dream.Tests\DedicatedServerServiceTests.cs dream.Tests\Server\DedicatedServerServiceTests.cs
```

- [ ] **Step 3: Apply test namespaces**

Use:

```csharp
namespace Dream.Tests.Simulation.World;
namespace Dream.Tests.Rendering.Actors;
namespace Dream.Tests.Multiplayer.Protocol;
```

Add explicit production usings:

```csharp
using Dream.Simulation.World;
using Dream.Rendering.Actors;
using Dream.Multiplayer.Protocol;
```

- [ ] **Step 4: Run full test suite**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false
```

Expected: all tests pass.

---

## Task 9: Add Dependency Direction Tests

**Files:**
- Create: `dream.Tests/ProjectStructure/DependencyDirectionTests.cs`

- [ ] **Step 1: Add static source dependency tests**

Create `dream.Tests/ProjectStructure/DependencyDirectionTests.cs`:

```csharp
using System.IO;
using Xunit;

namespace Dream.Tests.ProjectStructure;

public class DependencyDirectionTests
{
    [Fact]
    public void ServerSources_DoNotReferenceClientOrRenderingNamespaces()
    {
        AssertNoReferences(
            Path.Combine("dream", "Server"),
            [
                "Dream.Client.",
                "Dream.Rendering.",
                "ImGuiNET",
                "OpenTK.Windowing"
            ]);
    }

    [Fact]
    public void MultiplayerProtocolSources_DoNotReferencePresentationOrRenderingNamespaces()
    {
        AssertNoReferences(
            Path.Combine("dream", "Multiplayer", "Protocol"),
            [
                "Dream.Client.",
                "Dream.Rendering.",
                "ImGuiNET",
                "OpenTK.Graphics",
                "OpenTK.Windowing"
            ]);
    }

    private static void AssertNoReferences(string relativeFolder, IReadOnlyList<string> forbiddenTokens)
    {
        string repoRoot = FindRepoRoot();
        string folder = Path.Combine(repoRoot, relativeFolder);

        foreach (string file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            string content = File.ReadAllText(file);
            foreach (string forbiddenToken in forbiddenTokens)
            {
                Assert.DoesNotContain(forbiddenToken, content);
            }
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
```

- [ ] **Step 2: Run dependency direction tests**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~DependencyDirectionTests"
```

Expected: pass. If it fails, either move the type to the correct folder or introduce a narrower shared contract.

---

## Task 10: Update Documentation And Record The Migration

**Files:**
- Modify: `README.md`
- Modify: `docs/project-structure.md`
- Modify: `docs/architecture.md`
- Create: `docs/ledger/decisions/2026-07-01-code-organization-and-namespace-boundaries.md`
- Create: `docs/ledger/work-log/2026-07-01-code-organization-namespaces-and-folders.md`

- [ ] **Step 1: Add decision record**

Create `docs/ledger/decisions/2026-07-01-code-organization-and-namespace-boundaries.md`:

```markdown
Purpose: define durable source organization rules for Dream after the client/server multiplayer split
Status: accepted
Scope: C# namespaces, source folders, dependency direction, and future project split guidance

## Decision

Dream uses `Dream` as the root namespace and organizes production source by bounded context folders under the existing `dream.csproj`.

The first migration keeps one project file. A later multi-project split may happen only after folder and namespace boundaries are stable and dependency direction tests are green.

## Namespace Rules

- folder path maps to namespace path
- production namespaces start with `Dream`
- test namespaces start with `Dream.Tests`
- server code must not depend on client or rendering namespaces
- protocol code must not depend on client presentation, OpenGL, ImGui, or OpenTK windowing
- explicit local `using` directives are preferred over broad global aliases

## Rationale

The flat source root obscured the now-real client, server, protocol, simulation, and rendering boundaries. Folder and namespace boundaries make ownership clearer without forcing an immediate assembly split.
```

- [ ] **Step 2: Add work-log**

Create `docs/ledger/work-log/2026-07-01-code-organization-namespaces-and-folders.md`:

```markdown
Purpose: summarize the source-layout migration from a flat C# root to bounded context folders and namespaces
Status: completed
Scope: production source folders, test folders, namespace conventions, dependency direction tests

## Completed

- Added `Dream` and `Dream.Tests` namespace conventions.
- Moved source files from the flat `dream/` root into bounded context folders.
- Mirrored production organization in the test project.
- Added structure and dependency-direction tests.
- Updated project documentation to describe the new layout.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
```

- [ ] **Step 3: Update docs**

Update `docs/project-structure.md` to replace the flat file inventory with the new folder map from this plan.

Update `docs/architecture.md` to describe:

```markdown
- `Dream.Client.*` owns desktop startup, input, presentation, replication intake, and remote client networking.
- `Dream.Server.*` owns headless server bootstrap and the dedicated host loop.
- `Dream.Multiplayer.*` owns shared protocol, transport, session, and diagnostics seams.
- `Dream.Simulation.*` owns authoritative world/player/enemy/combat/physics state.
- `Dream.Rendering.*` owns OpenGL, model, actor, HUD, and viewmodel rendering.
```

- [ ] **Step 4: Final verification**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false
dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false
```

Expected: tests pass and build succeeds with only known warnings.

---

## Task 11: Evaluate Future Multi-Project Split

**Files:**
- Create: `docs/ledger/analysis/2026-07-01-client-server-project-split-readiness.md`

- [ ] **Step 1: Write readiness analysis**

Create `docs/ledger/analysis/2026-07-01-client-server-project-split-readiness.md`:

```markdown
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
```

- [ ] **Step 2: Do not create projects yet**

Stop at the analysis record unless the team explicitly approves the multi-project split.

---

## Final Verification

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false
dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false
```

Expected:

- all tests pass
- build succeeds
- known package compatibility warnings may remain
- no broad behavior changes are introduced

## Rollback Guidance

This migration should be committed in small slices. If a slice gets messy:

- revert only that slice's file moves and namespace edits
- keep earlier green slices intact
- avoid `git reset --hard`
- prefer non-interactive `git mv`, `git status`, `dotnet test`, and `dotnet build`

## Success Criteria

- `dream/` no longer contains a flat pile of source files.
- Client/server/protocol/simulation/rendering ownership is readable from folder path and namespace.
- Server and protocol layers are protected from client/rendering dependencies by tests.
- Tests mirror production folders enough that future agents can find coverage quickly.
- Documentation and ledger records explain the new layout and why the project split remains a later decision.
