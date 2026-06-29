# Phase 8 Integrated Local Server Hosting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Start one local authoritative server automatically in desktop mode and expose it as the default multiplayer target.

**Architecture:** Wrap the existing dedicated server stack in a desktop-owned lifecycle seam that starts on loopback in the background and exposes readiness, bound port, and failure state to the startup connection flow.

**Tech Stack:** C# / .NET 10, existing `DedicatedServerHost`, `DedicatedServerService`, `TcpServerTransport`, and desktop OpenTK shell

---

## File Structure Direction

- `dream/ILocalServerHandle.cs`
  - lifecycle contract
- `dream/IntegratedLocalServerHandle.cs`
  - background local server wrapper
- `dream/TcpServerTransport.cs`
  - actual bound-port exposure
- `dream/Game.cs`
  - create/dispose integrated local server handle

### Task 1: Support dynamic bound-port discovery

**Files:**
- Modify: `dream/TcpServerTransport.cs`
- Create: `dream.Tests/TcpServerTransportTests.cs`

- [ ] allow port `0` for automatic OS assignment
- [ ] expose the actual bound port after start
- [ ] keep existing explicit-port behavior intact

### Task 2: Add integrated local server lifecycle seam

**Files:**
- Create: `dream/ILocalServerHandle.cs`
- Create: `dream/IntegratedLocalServerHandle.cs`
- Create: `dream.Tests/IntegratedLocalServerHandleTests.cs`

- [ ] start the authoritative server stack in the background
- [ ] expose readiness, bound port, and startup failure
- [ ] dispose cleanly with cancellation

### Task 3: Wire desktop startup into local hosting

**Files:**
- Modify: `dream/Game.cs`
- Modify: `dream/ServerBootstrap.cs`

- [ ] auto-start the integrated local server in desktop mode
- [ ] use loopback-local defaults for the startup scene
- [ ] keep `--server` headless mode intact

## Self-Review Notes

- Spec coverage: covers local server startup, port discovery, and desktop lifecycle ownership
- Placeholder scan: responsibilities are concrete and limited
- Type consistency: `ILocalServerHandle` and `IntegratedLocalServerHandle` remain stable throughout
