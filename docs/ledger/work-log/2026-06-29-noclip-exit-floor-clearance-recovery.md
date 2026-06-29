Purpose: record the local collision fix for the case where disabling noclip while intersecting the floor left the player in an invalid falling state.
Status: completed
Scope: player motor noclip-exit recovery, focused regression coverage, and verification notes

# Summary

The client-local collision path still had one invalid-state hole after the earlier multiplayer and camera fixes: if the player disabled noclip while the rigid body had already been moved below the valid floor-supported center height, the next physics updates could continue from that bad overlap and the player would keep falling through the floor.

# Root Cause

- `PlayerMotor` used direct transform writes while noclip was active.
- When noclip turned off, the code restored contact response and gravity, but it did not repair the body transform if the player was already below the valid floor-supported center height.
- That left Bullet to recover from an already-invalid overlap state, which was not reliable for this transition.

# Changes

- Added a focused `PlayerMotorTests` regression that reproduces disabling noclip while the body is inside the floor and asserts the body is restored to a valid grounded height.
- Updated `PlayerMotor` to restore a minimum world-floor clearance on noclip exit before the next normal physics step continues.
- Kept the fix narrow to the noclip-exit transition so ordinary grounded movement, jumping, and midair falling behavior stay unchanged.

# Verification

- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`

# Notes

- The build and tests still report the known `NU1701` warning for `BulletSharp`.
- This fix addresses the floor-clearance hole specifically. Future collision recovery work can extend the same idea to broader depenetration or support-surface recovery if noclip exit later needs to account for more complex solid geometry than the base floor.
