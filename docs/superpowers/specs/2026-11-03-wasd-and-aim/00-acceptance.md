# WASD movement, mouse-aimed facing, and hold-to-attack

Status: written 2026-11-03, before implementation. Depth: cross-cutting (input, facing, animation,
combat timing). Claim tags: VERIFIED = read in the code; INFERRED = not yet confirmed.

## What the user asked for

1. Bring back W/A/S/D movement.
2. Move and face independently: pressing A with the cursor to the right walks left while the
   character keeps facing the cursor.
3. The first mouse click attacks, and holding the button attacks on a cycle until release.

## The ground truth, which makes this much smaller than it looks

VERIFIED. The protocol and the server already carry all three. The isometric client simply stopped
sending them when it moved to click-to-move:

- `PlayerController.CaptureCommandFrame` hardcodes `forwardAxis: 0f, strafeAxis: 0f, yawDelta: 0f,
  primaryUsePressed: false`, with the comment "Keys only: walking, looking and the primary action
  come from clicks (ClickMoveController)". The fuller `CreateCommandFrame` takes all of them and is
  public.
- `PlayerMoveIntent(ForwardAxis, StrafeAxis, VerticalAxis, ...)`, `PlayerLookIntent(YawDelta,
  PitchDelta)` and `PrimaryUsePressed` are live fields on `PlayerCommandFrame`.
- **Facing is already replicated** - `ActorFieldGroups.Facing`, quantised to 1/4096, absolute on a
  full frame and delta otherwise (`ActorFrameJson.cs:23`). Decoupled facing therefore reaches other
  clients with **no protocol change**.
- `IClickMoveHost` already exposes `void SetFacing(Vector3 direction)`, which click-to-move drives.
- `PlayerCommandOneShotInputFilter` edge-triggers Jump, Freeze and Noclip. INFERRED: it does not
  currently edge-trigger the primary action - to confirm.

So this is client input wiring against an existing server contract, not a protocol or simulation
change. That is the single most important fact for scoping it.

## Acceptance criteria

**B1 - WASD moves.** W/A/S/D drive `forwardAxis`/`strafeAxis` in the isometric view, resolved in
**camera space** (W goes up-screen, not along world +Z, since the camera is yawed 45 degrees).
Diagonals are normalised so W+A is not faster than W. Movement works while the cursor is stationary.

**B2 - facing follows the cursor, independently of movement.** With the cursor to the right and A
held, the character translates left while facing right. Facing is driven by the cursor's world-space
ground position, not by the movement vector. It updates while moving, while idle, and while
attacking. Other clients see the same facing (it already replicates).

**B3 - the animation does not moonwalk.** Moving backwards or sideways relative to facing must not
play a forward-walk clip as though the character were facing its direction of travel. State the
chosen answer explicitly - directional clips, a counter-rotated lower body, or accepting it for now.
This is the one genuinely structural question.

**B4 - hold to attack.** Press attacks once immediately; holding repeats on the existing attack
cadence until release; releasing stops. No burst on release, and the repeat rate is the server's
cooldown, not the frame rate. Must respect the server's action budget rather than flooding it.

**B5 - click-to-move still works, or is deliberately retired.** Both inputs currently want the mouse.
State whether left-click becomes attack-only and click-to-move is dropped, or whether they coexist
on different buttons. The user asked for the first click to be "a hitting one", which INFERRED means
left-click is attack.

**B6 - nothing regresses.** Build clean; no new test failures against master's 43 pre-existing;
first-person and third-person input unchanged; the `DREAM_HUD_CAPTURE_SCRIPT` key and click steps
(`keys:w+a`, `screenclick`, `walkto`, `fight`) still work, since they drive the real input path.

## Non-goals

- No protocol or wire-format change. Facing already replicates; the axes already exist.
- No change to the server's movement simulation, collision or nav.
- No new animation clips authored this round unless B3's answer demands it.
- No change to first-person or third-person input mapping.
- No rebalancing of attack damage, range or cooldown; only when an attack is *issued*.

## Open questions

- Q1. Does click-to-move survive? It and mouse-aim both want the cursor, and `ClickMoveController`
  owns attack-on-target, build placement and `SetFacing` today. B5 decides how much of it remains.
- Q2. Is the primary action edge-triggered anywhere between the client and the server's cooldown? B4
  depends on the answer.
- Q3. Does the character's pitch matter for aim, or is facing yaw-only on the ground plane?

## Verification record, 2026-11-04, on integrate/input-and-hud @ d97807c

This file was written before the work started and then left UNTRACKED for the whole round. Every
agent worked in a worktree, where an untracked file does not exist, so nobody could read these
criteria and the runtime pass was verified against a re-worded brief instead. Commit the acceptance
file before launching the first agent; that is the whole point of writing it first.

MEASURED in-engine, captures under the integration worktree's `artifacts/`:

- **B1 + B2 PASS.** Holding A for 90 frames with the cursor latched right: travel 8.82 m along a
  single fixed direction while yaw held at -48 and `aimOwnsFacing True`. Reproduced over four
  alternating A/W legs. NOTE: the travel direction is the isometric screen-left diagonal
  (-0.707, +0.707), not a world axis - WASD is camera-relative, so "moves along one axis" is the
  wrong test for this camera. A fixed world *direction* is the criterion.
- **B3 normalisation PASS.** W 5.84 m vs W+A 5.75 m over equal 60-frame holds (1.6%); W+A resolves
  to pure -X with no Z drift.
- **B3 moonwalk NOT VERIFIED, and the harness cannot verify it.** `HudCaptureVerb.Keys` sets
  `_captureWritesPng = false` and releases the keys when the step ends, so no PNG is ever written
  while a movement key is held. The closest frame is one render frame after release, and at
  `DREAM_HUD_CAPTURE_SETTLE=1` a strafe frame and a forward frame are indistinguishable; the only
  variation found between two strafe frames was stride phase. Settling this needs a capture step
  that writes a PNG with keys still held.
- **B4 PASS on all three sub-claims.** Attack interval 0.426 s; first hit 83 ms after the press,
  then +434 ms and +435 ms. The decisive negative: a 10-frame hold logged one hit and no second hit
  through frame 31, past where interval #2 at 426 ms would have landed.
- **B5 PASS.** Right-button click-to-move paths and walks (three `walkto` runs `reached=true`); a
  left click over the world leaves position byte-identical with `goal None`, and the same left
  press is what lands every B4 hit.
- **B6 PASS**, established by the integrator rather than by capture: 32 distinct failing test names
  byte-identical across clean master, the 4-branch and the 5-branch merges, with +79 new passing
  tests. No new failures.

Carried forward, neither caused by nor blocking this round:

- **A3 actor internal spread 0.2314 against a 0.45 floor.** Pre-existing; `check_look.py`'s own
  comment records the baseline as 0.278, and this frame's figure stands in a dim patch rather than
  a brazier pool. It is the art gap the models wave exists to close, not a regression from input
  or HUD work.
- **A1 bright share 0.028 against a 0.020 floor is thin, and it moves with the HUD crop.** At
  `--hud-rows 96` (the literal bar rect, `UiLayout.BarHeightUnits 48` x scale 2.0) it passes; at
  190, which also crops the gargoyle ornaments rising above the bar, it falls to 0.018 and A1
  fails. The gate needs a defined crop, not a judgement call per run.
- **Village pathing friction.** `walkto 43,45` needed 34 clicks and 97.6 m walked for a ~33 m
  straight line, against 6 clicks / 36.1 m and 3 clicks / 11.1 m on legs outside the village. It
  reached, so this is not a failure, but it crosses the newly propped spawn and prop collision is
  the obvious suspect.
- **Mean saturation did not regress.** Flagged as the likeliest casualty of the village props at
  0.372 against the 0.38 ceiling; the real post-merge number is 0.274, with 0.106 of headroom.
