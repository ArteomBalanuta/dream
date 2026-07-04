# Protocol Parity For All Desktop Clients

Purpose: define the target architecture where every human-controlled desktop player uses the multiplayer protocol, including the local host.
Status: Approved for implementation
Scope: desktop startup, client connection ownership, integrated local server transport, authoritative tick intake, and removal of host-only gameplay authority paths.

## Goal

Every playable desktop client must communicate with the authoritative server through the same client/server protocol. The first local player, later local windows, and remote clients should differ only by transport endpoint, not by gameplay ownership rules.

## Problem

The desktop host path has drifted from joined-client behavior over time:

- local desktop play can still construct `PlayScene` with no `RemoteClientSession`
- the old no-remote branch directly advances `WorldSessionRuntime`
- joined players rely on the dedicated-server command/action/snapshot path
- bug fixes can land in one path while the other path keeps different behavior

The recent secondary-player enemy-damage bug was a concrete symptom: the host path generated primary-use combat from commands, while secondary sessions needed an extra server-tick bridge.

## Target Architecture

- The authoritative server is always the only component that advances gameplay truth.
- A desktop gameplay scene always has a protocol client session.
- The local host connects to the integrated server through a loopback client session.
- The initial implementation may continue using TCP loopback, but the intended local transport is an in-memory duplex transport that still exercises the same protocol codec, message routing, command inbox, action queue, and snapshot intake.
- Client-side `WorldSessionRuntime` remains only as a presentation mirror until it can be replaced by narrower client presentation state.

## Protocol Rule

If a value changes authoritative gameplay truth, it must flow through protocol:

- movement, look, mode, and primary-use input use `PlayerCommandFrame`
- one-shot HUD/gameplay actions use `PlayerActionMessage`
- authoritative results arrive through `ServerActionMessage` and `ServerSnapshotMessage`

Presentation-only values such as FOV, mouse sensitivity, resolution, and floating combat text tuning can stay client-local.

## Non-Goals For First Slice

- Replacing TCP loopback with in-memory transport in the same patch.
- Replacing the client-side mirrored runtime completely.
- Promoting every joined player to a full Bullet-backed `Player` body.
- Protocolizing floor-size edits immediately.

## Required Follow-Up

- Add in-memory client/server transport for integrated local play.
- Make floor-size and other gameplay-affecting settings either server-owned protocol actions or disabled in connected play.
- Continue the full multi-client actor-equivalence work so no server-side player remains privileged by actor depth.
