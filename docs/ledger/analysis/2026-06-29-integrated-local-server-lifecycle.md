# Integrated Local Server Lifecycle

Purpose: define how the desktop app should own a local authoritative server instance for local multiplayer hosting.
Status: complete
Scope: local server startup timing, port binding, background execution, shutdown, and interaction with the boot-time connection flow

## Goal

Launch a local authoritative server automatically during desktop startup so the player can immediately connect through the normal client/server path.

## Recommended Lifecycle

### Startup

Desktop mode should:

- create the authoritative server host
- create a loopback-bound TCP transport
- start the server service in the background
- expose the bound port to the startup connection scene

Using port `0` is preferred for the integrated local server so the OS can choose an available port automatically and avoid common local collisions.

### Runtime

The integrated local server should:

- keep accepting clients in the background
- keep running regardless of whether the local desktop client has connected yet
- expose a simple readiness flag and bound-port fact
- expose startup failure state cleanly

### Shutdown

When the desktop app closes, the integrated local server should:

- cancel its background run loop
- dispose the server service
- dispose the transport
- dispose the host

## Required Seams

- `ILocalServerHandle`
  - narrow lifecycle and readiness contract
- `IntegratedLocalServerHandle`
  - concrete desktop-owned implementation using the current `DedicatedServerService`

## Why This Fits The Current Repo

- the repo already has a dedicated server service and authoritative runtime host
- the desktop client already has a transport-based remote connection path
- local hosting should use that same path instead of inventing a client bypass

## Recommendation

Auto-start one integrated loopback server in desktop mode and let the startup connection scene treat it as the default target. That produces the cleanest working local multiplayer path while remaining compatible with later dedicated remote hosting.
