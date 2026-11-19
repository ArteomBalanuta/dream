namespace Dream.Rendering.Actors;

// The sculpted primitive one part of a held or worn model is drawn with.
//
// Before this, every part of every weapon and every armour piece was one axis-aligned unit cube, so
// the whole equipment set read as stacked blocks: flat facets, hard 90-degree edges, no curvature
// for the light to run along. A shape replaces that cube with a small sculpted mesh built once by
// EquipmentShapeLibrary; the part's Center / Size / Pitch / Roll / Yaw place and stretch it exactly
// as they placed the cube, so an existing part keeps its mass and its place and only changes form.
//
// CONTRACT (EquipmentShapeLibrary.Build, pinned by EquipmentShapeLibraryTests):
//   - every shape is a closed-ish mesh whose vertices lie inside [-0.5, 0.5] on all three axes, and
//     whose bounding box touches both -0.5 and +0.5 on at least one axis, so a part's authored Size
//     is still its real extent;
//   - triangle winding is counter-clockwise seen from outside (the world pass draws with culling off,
//     but normals must still point outward or the lighting inverts);
//   - every normal is unit length and outward; a face meant to read as a facet gets a split (flat)
//     normal, a face meant to read as a curve gets an averaged (smooth) normal;
//   - UVs lie in [0, 1] and are continuous per face; nothing samples a texture today, but the vertex
//     layout carries them and a future wear map will;
//   - Box is EXACTLY the old unit cube, so a part that names no shape draws byte-for-byte as before.
//
// Which shapes FILL their box: every one except Ring, and every shape stays centred on the origin.
// Ring is a torus, so it fills X and Z while its Y is the MINOR diameter, 0.30 of the authored
// Size.Y. Every extent is pinned by EquipmentShapeLibraryTests. The exception matters in two
// places: HeldItemScale measures a model's authored BOXES, so a Ring at the extreme end of a weapon
// makes the drawn length a few percent shorter than the documented one (sub-pixel at the shipped
// zoom); and the armour cover gates measure boxes too, so a Ring used as the only cover of a body
// region covers less than the gate believes.
//
// Length axis: everything that has a length runs along +Y, like the old parts (a weapon's length is
// +Y, a limb's length is +Y). The broad side faces +-Z and the thin side +-X, again as before.
public enum EquipmentShape
{
    // The legacy axis-aligned cube. Default, so untouched content is unchanged.
    Box = 0,

    // A cube with its twelve edges and eight corners chamfered. The single biggest change in how a
    // block reads: the chamfer catches a different light value than the faces, so a straight piece of
    // plate or a haft block stops looking like a voxel without changing its silhouette.
    BevelBox,

    // A rounded, slightly barrelled column along Y with capped ends: hafts, shafts, poles, cage bars,
    // rolled leather grips.
    Shaft,

    // A truncated cone along Y, wide at -Y and narrow at +Y: ferrules, collars, quillon tips, spike
    // bases, the tapering part of a greave.
    Taper,

    // A sword blade along Y: a lenticular (diamond) cross-section with a raised central ridge, a
    // bevelled cutting edge down each side, a slight distal taper and a clipped point near +Y.
    Blade,

    // A curved shell: a cylindrical arc of about 230 degrees about the Y axis, open at the back, with
    // a real wall thickness and a rolled lip. The workhorse of plate armour - cuirass halves,
    // greaves, bracers, helm skirts - because it wraps a limb instead of boxing it.
    //
    // WHICH WAY IT OPENS MATTERS, and getting that wrong is the one mistake this shape invites. The
    // shell's axis is +Y and its opening faces -Z, so unrotated it wraps something that runs
    // vertically and leaves the BACK bare. That is right for a cuirass or a greave, where the baked
    // body fills the gap, and wrong for anything that caps a limb: used unrotated as a shoulder cap
    // it presents its hollow back to a camera that looks down and forward, and reads as a detached
    // horseshoe floating beside the figure instead of as armour on a shoulder. That is exactly what
    // the first cut of the big pauldrons looked like in the engine.
    //
    // To CAP something - a shoulder, a knee, a hip - either use Dome, which is closed and cannot
    // show a hollow, or turn the opening downward with `pitch: -90f`, which puts the axis
    // front-to-back and the opening at -Y so the gap faces the body. Rotations apply as pitch, then
    // roll, then yaw (ArmourPart.ToMatrix and WeaponPart.ToMatrix), so a later roll cannot undo a
    // pitch: work the orientation out in that order.
    Plate,

    // A hemi-ogive capping +Y on a short cylindrical skirt: helm skulls, shield bosses, pommels,
    // rivet heads, the dome of a lantern.
    Dome,

    // A sharp four-sided pyramid along +Y with a slight entasis: studs, crown spikes, mace flanges,
    // the barbs on a gothic pauldron.
    Spike,

    // A torus lying in the XZ plane (its hole on the Y axis): crown bands, pommel rings, shield rims,
    // buckle rings, the collar of a gorget.
    Ring,

    // A swept crescent in the YZ plane, thin in X, with a ground bevel along its convex edge and a
    // tapered horn at each end: axe bits, glaive edges, the sweep of a gothic guard.
    Crescent,

    // A faceted bipyramid with a table facet on +Y: gems, arcane stones, ember coals. Its hard facets
    // are on purpose - they break the light into separate values, which is what reads as a jewel.
    Gem,

    // A thin, gently curved and wavy slab in the XY plane: cloaks, tabards, mantles, banners, wings.
    // The waves and the curve give cloth a run of light instead of one flat value.
    Panel,

    // A CURVED, tapering horn: a cone swept along an arc, thick at -Y, sweeping toward +Z as it
    // rises and ending in a point at +Y. Spike is the straight version of this and reads as a stud;
    // Horn reads as a horn, a claw, a crest point or the upswept barb of a pauldron, because the
    // curve puts the silhouette's weight somewhere other than straight up. Yaw and roll aim it.
    //
    // This is the single most characteristic element of the dark-fantasy armour the project is aimed
    // at: the shoulders carry two or three of them, the helm carries a crest of them, and they are
    // what makes an outline read as armour from a distance rather than as a mass.
    Horn,

    // A bell: narrow at -Y, widening along a CONCAVE profile to a flared lip at +Y, hollow, with a
    // wall and a rolled rim. The end of a gauntlet cuff, the top of a boot, the skirt of a fauld,
    // the brim of a helm, the trumpet of a tasset.
    //
    // It exists because a limb piece that is a straight tube reads as a pipe. Every reference the
    // direction comes from flares at the ends of things - cuffs, boot tops, hems - and that flare is
    // most of what separates armour from clothing in a silhouette 28 pixels wide.
    Flare,

    // ---- pass 4: the profiled shells -------------------------------------
    //
    // Plate, the workhorse, is a CYLINDER: one radius and one sweep from rim to rim. Wrapped round a
    // forearm that is right; used as a shoulder cap or a chest it gives a slab, because the eye reads
    // a constant section as an extruded shape however curved its ends are. The four shells below are
    // Plate with a RING TABLE - radius, sweep and centre varying per ring - which is the technique the
    // low-poly armour generator skill is built on. Each one opens toward -Z exactly as Plate does, so
    // the Plate rule holds: to CAP something, turn the opening downward with pitch -90.

    // A shoulder cap: nearly closed and wide at the top, swelling to its widest a third of the way
    // down, then tucking IN and opening out at the hem, so it throws a shadow under itself and sits
    // ON the shoulder instead of boxing it. This is the pauldron shape; Plate was never one.
    Pauldron,

    // A torso: an hourglass with an ELLIPTICAL section - wide chest, pinched waist, flared fauld,
    // and shallower front-to-back than it is wide, because a body is. A cuirass built as a Plate is
    // a barrel.
    Cuirass,

    // A limb shell that TAPERS and LEANS: wide at +Y (the knee, the elbow), narrow at -Y, with the
    // centre walking toward +Z as it falls. A greave, a bracer, a vambrace, a boot shaft. A tube
    // reads as a pipe; the taper and the lean are the fix.
    Greave,

    // Three stacked lames with hard steps between them, each standing a little further out than the
    // one below: a fauld, a tasset, a couter, a gorget. At the shipped zoom what reads is three
    // value steps in a row, and that is what layered plate looks like from the game camera.
    Lame,

    // A RIBBED sheet: a wing, a fin, a tattered banner. Panel is a cloth sheet whose whole surface
    // variation is one gentle bulge, which at twenty pixels across is one value - that is why the
    // gossamer wings read flat. Membrane runs four ribs along its length with the normal split at
    // every ridge, which is a value change every five pixels and survives the resolve. Like Panel it
    // is a sheet, so its Z is thickness plus camber and does not fill the box.
    Membrane,
}
