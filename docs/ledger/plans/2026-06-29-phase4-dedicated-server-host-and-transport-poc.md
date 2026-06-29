# Phase 4 Dedicated Server Host And Transport PoC Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the first headless dedicated-server host path and the fastest working transport edge needed to connect clients to one authoritative world session.

**Architecture:** Keep `WorldSessionRuntime` as the host-agnostic authoritative core, add a server bootstrap and server host around it, and use a narrow TCP-style transport interface for the first PoC without baking transport details into gameplay code.

**Tech Stack:** C# / .NET 10, BulletSharp authoritative runtime, desktop OpenTK client shell, TCP-style loopback/local transport for PoC

---

### Task 1: Split server bootstrap from desktop bootstrap

**Files:**
- Create: `dream/ServerBootstrap.cs`
- Create: `dream/DedicatedServerHost.cs`
- Modify: `dream/Program.cs`
- Modify: `docs/architecture.md`

- [ ] Add a `--server` startup path that does not construct `Game`, `PlayScene`, or any render/HUD systems
- [ ] Keep desktop startup as the default path
- [ ] Make process-mode selection explicit in `Program.cs`
- [ ] Preserve the current desktop startup behavior unchanged outside the new branch

### Task 2: Wire headless host ownership around the runtime

**Files:**
- Modify: `dream/WorldSessionRuntime.cs`
- Create: `dream/ServerTickRunner.cs`
- Create: `dream/ServerConnection.cs`
- Modify: `dream/PhysicsIntegrator.cs`
- Modify: `dream/PlayerSessionRegistry.cs` after Phase 2 lands

- [ ] Let `DedicatedServerHost` own `WorldSessionRuntime`, `PlayerSessionRegistry`, and fixed tick execution
- [ ] Remove any remaining assumption that authoritative runtime work requires scene or render ownership
- [ ] Preserve disposal and runtime cleanup behavior in the headless path
- [ ] Keep room for many player sessions instead of one constructor-owned player body

### Task 3: Add narrow transport interfaces and message framing

**Files:**
- Create: `dream/IServerTransport.cs`
- Create: `dream/IClientTransport.cs`
- Create: `dream/TcpServerTransport.cs`
- Create: `dream/TcpClientTransport.cs`
- Create: `dream/TransportEnvelope.cs`
- Create: `dream/TransportJsonCodec.cs`

- [ ] Introduce the smallest transport interfaces needed for connect, disconnect, send, and receive
- [ ] Support only the first PoC message families: join, disconnect, `PlayerCommandFrame`, and `ServerSnapshotEnvelope`
- [ ] Keep serialization and framing out of gameplay/runtime types
- [ ] Choose readability and debuggability over final payload efficiency

### Task 4: Attach the desktop client to the transport edge

**Files:**
- Modify: `dream/PlayScene.cs`
- Modify: `dream/Game.cs`
- Modify: `dream/WorldSessionRuntime.cs`
- Modify: `docs/project-structure.md`

- [ ] Let the local client path submit command frames through `IClientTransport`
- [ ] Let the local client path receive authoritative snapshots back without reclaiming runtime authority
- [ ] Keep transport ownership separate from HUD and viewmodel systems
- [ ] Preserve a local development path that can still run without remote transport if needed for diagnostics

### Task 5: Verify and document the PoC transport slice

**Files:**
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Create: `docs/ledger/work-log/2026-06-29-phase4-dedicated-server-host-and-transport-poc.md`

- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Run `dotnet run --project dream\\dream.csproj -- --server` as a headless boot smoke test
- [ ] Record whether two local desktop clients could connect to the same server session and what limitations remain

## Self-Review Notes

- Spec coverage: this phase covers server bootstrap, headless host ownership, transport interfaces, client attachment, and verification
- Placeholder scan: the plan uses concrete file targets and bounded PoC message families
- Type consistency: `DedicatedServerHost`, `ServerBootstrap`, `IServerTransport`, `TcpServerTransport`, and `TransportEnvelope` remain the core seams throughout
