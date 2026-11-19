"""Golden compare tests. Run: python -I tools/golden/tests/test_golden.py"""
import re
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import golden  # noqa: E402


def save(path: Path, pixels: np.ndarray) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(pixels).save(path)


class GoldenCompareTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)
        self.scene = {"name": "t", "pixelThreshold": 0.08, "maxFraction": 0.002, "ignore": []}
        self.base = self.root / "base"
        self.out = self.root / "out"

    def record(self, frame=None, scene=None, noisy_b=None, runs=4):
        """Record `runs` captures of `frame`; the last one is `noisy_b` when given (one odd run among the rest)."""
        frame = golden.synthetic_frame() if frame is None else frame
        dirs = []
        for i in range(runs):
            d = self.root / f"r{i}"
            save(d / "s1.png", noisy_b if (noisy_b is not None and i == runs - 1) else frame)
            dirs.append(d)
        return golden.record(dirs, scene or self.scene, self.base)

    def run_compare(self, frame, scene=None, name="actual"):
        actual = self.root / name
        save(actual / "s1.png", frame)
        return golden.compare(actual, scene or self.scene, self.base, self.out)

    def test_identical_passes(self):
        self.record()
        results = self.run_compare(golden.synthetic_frame())
        self.assertTrue(all(ok for ok, _ in results), results)
        self.assertTrue(results[0][1].startswith("PASS t s1.png"))

    def test_small_noise_passes(self):
        self.record()
        rng = np.random.default_rng(1)
        noisy = golden.synthetic_frame().astype(np.int16) + rng.integers(-6, 7, (200, 320, 3))
        results = self.run_compare(np.clip(noisy, 0, 255).astype(np.uint8))
        self.assertTrue(all(ok for ok, _ in results), results)

    def test_moved_bar_fails_and_writes_a_diff_png(self):
        self.record()
        results = self.run_compare(golden.synthetic_frame(bar_x=180))
        self.assertFalse(results[0][0], results)
        self.assertTrue(results[0][1].startswith("FAIL t s1.png"))
        diff = self.out / "t" / "s1-diff.png"
        self.assertTrue(diff.exists())
        pixels = np.asarray(Image.open(diff).convert("RGB"))
        red = (pixels[..., 0] == 255) & (pixels[..., 1] == 0)
        # old bar position (x 100..120) and new one (x 180..200), 60 rows, at half resolution
        self.assertGreaterEqual(int(red.sum()), 2 * 10 * 30 - 100)

    def test_size_mismatch_fails(self):
        self.record()
        results = self.run_compare(golden.synthetic_frame(size=(400, 200)))
        self.assertFalse(results[0][0])
        self.assertIn("size mismatch", results[0][1])

    def test_missing_baseline_and_step_count_mismatch_fail(self):
        results = self.run_compare(golden.synthetic_frame())
        self.assertFalse(results[0][0])
        self.assertIn("no baseline", results[0][1])
        self.record()
        extra = self.root / "extra"
        save(extra / "s1.png", golden.synthetic_frame())
        save(extra / "s2.png", golden.synthetic_frame())
        results = golden.compare(extra, self.scene, self.base, self.out)
        self.assertFalse(results[0][0])
        self.assertIn("step mismatch", results[0][1])

    def test_ignore_rect_masks_a_change_and_only_that_change(self):
        # The bar moved from x=100 to x=180; ignoring both its old and new places (full-res rects) passes,
        # ignoring only the old place still fails.
        self.record()
        moved = golden.synthetic_frame(bar_x=180)
        both = dict(self.scene, ignore=[[96, 56, 28, 68], [176, 56, 28, 68]])
        self.assertTrue(all(ok for ok, _ in self.run_compare(moved, both)))
        one = dict(self.scene, ignore=[[96, 56, 28, 68]])
        self.assertFalse(all(ok for ok, _ in self.run_compare(moved, one)))

    def test_effective_limit_is_max_of_floor_and_k_times_noise(self):
        k = golden.NOISE_MULTIPLIER
        # k = 3: the worst held-out ratio over 18 recorded runs per scene was 1.37 (README, "Choosing the multipliers")
        self.assertEqual(k, 3.0)
        self.assertEqual((golden.TONE_MULTIPLIER, golden.BLOCK_MULTIPLIER), (3.0, 5.0))
        self.assertEqual(golden.effective_limit(0.002, 0.0), 0.002)
        self.assertEqual(golden.effective_limit(0.002, 0.002 / k / 2), 0.002)
        self.assertAlmostEqual(golden.effective_limit(0.002, 0.01), k * 0.01)

    def test_record_needs_three_runs_and_stores_the_median(self):
        with self.assertRaises(SystemExit):
            self.record(runs=2)
        # one odd run of four: the per-pixel median is the common frame, not the odd one
        a = golden.synthetic_frame()
        odd = a.copy()
        odd[10:30, 10:60] = 255
        self.record(frame=a, noisy_b=odd)
        stored = np.asarray(Image.open(self.base / "t" / "s1.png").convert("RGB"))
        expected = golden.downscale2(a.astype(np.float32) / 255.0) * 255.0
        self.assertLess(int(np.abs(stored.astype(np.float32) - expected).max()), 2)

    def test_record_derives_noise_and_the_limit_widens_the_gate(self):
        # B has a small bar-sized difference from A: the recorded noise raises the limit above the floor.
        a = golden.synthetic_frame()
        b = a.copy()
        b[10:30, 10:60] = 255
        noise = self.record(frame=a, noisy_b=b)
        self.assertGreater(noise, 0.0)
        stored = golden.json.loads((self.base / "t" / "noise.json").read_text())
        self.assertAlmostEqual(stored["selfDiffFraction"], noise)
        loud = a.copy()
        loud[10:30, 10:60] = 255  # the same change as the noise: within k x noise
        self.assertTrue(all(ok for ok, _ in self.run_compare(loud, name="loud")))
        # Same recording but noise-free A/B: the same change now exceeds the floor and fails.
        self.record(frame=a)
        self.assertFalse(all(ok for ok, _ in self.run_compare(loud, name="loud2")))

    def test_per_step_noise_sets_each_steps_own_limit(self):
        # step s1 has one odd run among four (noisy), step s2 is identical in all runs (quiet)
        a = golden.synthetic_frame()
        odd = a.copy()
        odd[10:30, 10:60] = 255
        dirs = []
        for i in range(4):
            d = self.root / f"r{i}"
            save(d / "s1.png", odd if i == 3 else a)
            save(d / "s2.png", a)
            dirs.append(d)
        golden.record(dirs, self.scene, self.base)
        noise = golden.json.loads((self.base / "t" / "noise.json").read_text())
        self.assertGreater(noise["perStep"]["s1.png"], 0.0)
        self.assertEqual(noise["perStep"]["s2.png"], 0.0)
        self.assertAlmostEqual(noise["selfDiffFraction"], noise["perStep"]["s1.png"])
        actual = self.root / "actual"
        save(actual / "s1.png", a)
        save(actual / "s2.png", a)
        lines = [line for _, line in golden.compare(actual, self.scene, self.base, self.out)]
        limits = [float(line.split()[4]) for line in lines]
        self.assertAlmostEqual(limits[0], golden.effective_limit(0.002, noise["perStep"]["s1.png"]))
        self.assertAlmostEqual(limits[1], 0.002)  # the quiet step is NOT widened by the noisy one
        self.assertGreater(limits[0], limits[1])

    def test_tone_limit_is_derived_from_recorded_spread_with_a_floor(self):
        dev = np.array([0.0, 0.001, 0.02, 0.05, 0.2, 0.0, 0.0, 0.0, 0.0])
        lim = golden.tone_limits(dev)
        self.assertAlmostEqual(lim[0], golden.TONE_FLOOR)
        self.assertAlmostEqual(lim[2], max(golden.TONE_FLOOR, golden.TONE_MULTIPLIER * 0.02))
        self.assertAlmostEqual(lim[4], golden.TONE_MULTIPLIER * 0.2)
        # recorded: runs that differ in brightness widen the limit of the statistics they move
        a = golden.synthetic_frame()
        dim = np.clip(a.astype(np.float32) * 0.9, 0, 255).astype(np.uint8)
        dirs = []
        for i in range(4):
            d = self.root / f"r{i}"
            save(d / "s1.png", dim if i == 3 else a)
            dirs.append(d)
        golden.record(dirs, self.scene, self.base)
        stored = golden.json.loads((self.base / "t" / "noise.json").read_text())["tone"]["s1.png"]
        self.assertGreater(stored[0], 0.05)  # mean luma moved ~10% between runs

    def test_global_darkening_fails_on_the_tone_check_alone(self):
        self.record()
        dark = np.clip(golden.synthetic_frame().astype(np.float32) * 0.8, 0, 255).astype(np.uint8)
        results = self.run_compare(dark)
        self.assertFalse(results[0][0], results)
        self.assertIn("tone", results[0][1].split("<- failed:")[-1])
        # at a pixel threshold so high that no pixel counts, the pixel check is blind yet the compare still fails
        blind = dict(self.scene, pixelThreshold=0.9)
        self.assertFalse(self.run_compare(dark, blind, name="blind")[0][0])

    def test_a_missing_small_figure_fails_on_the_block_check(self):
        self.record()
        gone = golden.synthetic_frame()
        gone[60:120, 100:120] = gone[130:190, 100:120]  # the bar replaced by the ground below
        results = self.run_compare(gone, dict(self.scene, maxFraction=1.0))  # pixel floor wide open
        self.assertFalse(results[0][0], results)
        self.assertIn("block", results[0][1].split("<- failed:")[-1])

    def test_selftest_passes(self):
        self.assertEqual(golden.selftest(), 0)


class RealBaselineTests(unittest.TestCase):
    """The committed baselines, with a doctored frame: the gate must still be able to fail on real pixels."""

    SCENES = ("spawn-noon", "spawn-night", "wolf-staged")
    # Player figure (28x101 px incl. torch flame) on the 1440x1024 capture, and the ground 110 px below it.
    FIGURE = (710, 468, 28, 101)

    def doctored(self, scene_name, edit=None, every_step=False):
        """Compare the committed baseline (upscaled to capture size) after edit(full_res_frame) in place."""
        scene = golden.load_scene(scene_name)
        baseline = golden.list_pngs(golden.BASELINES / scene_name)
        self.assertTrue(baseline, f"no committed baseline for {scene_name}: record with tools/golden/run.ps1 -Record")
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        actual = Path(tmp.name) / "actual"
        for path in baseline:
            half = golden.read_rgb(path)
            full = np.repeat(np.repeat(half, 2, axis=0), 2, axis=1)
            if edit is not None and (every_step or path == baseline[-1]):
                edit(full)
            save(actual / path.name, (np.clip(full, 0, 1) * 255.0 + 0.5).astype(np.uint8))
        return scene, golden.compare(actual, scene, golden.BASELINES, Path(tmp.name) / "out")

    @staticmethod
    def paint(x, y, w, h, value):
        def edit(full):
            full[y:y + h, x:x + w] = value
        return edit

    @classmethod
    def remove_figure(cls, rect=None):
        x, y, w, h = rect or cls.FIGURE

        def edit(full):
            full[y:y + h, x:x + w] = full[y + 110:y + 110 + h, x:x + w]  # the ground just below
        return edit

    def test_undoctored_baseline_passes_itself(self):
        for name in self.SCENES:
            _, results = self.doctored(name)
            self.assertTrue(all(ok for ok, _ in results), results)

    def test_a_20_percent_global_darkening_fails_every_step_of_every_scene(self):
        # The per-pixel threshold alone is blind to this (0% of pixels move by > threshold); the tone check is not.
        def darken(full):
            full *= 0.8
        for name in self.SCENES:
            _, results = self.doctored(name, darken, every_step=True)
            for ok, line in results:
                self.assertFalse(ok, line)
                self.assertIn("tone", line.split("<- failed:")[-1], line)

    def test_a_40_percent_global_darkening_fails_every_scene(self):
        # the reviewer's x0.6 case (0.83% of pixels vs a 1.11% night limit used to PASS)
        def darken(full):
            full *= 0.6
        for name in self.SCENES:
            _, results = self.doctored(name, darken, every_step=True)
            self.assertFalse(any(ok for ok, _ in results), (name, results))

    def test_removing_the_player_figure_fails_noon_and_wolf(self):
        for name in ("spawn-noon", "wolf-staged"):
            _, results = self.doctored(name, self.remove_figure())
            self.assertFalse(results[-1][0], (name, results[-1]))
            self.assertIn("block", results[-1][1].split("<- failed:")[-1], results[-1])

    def test_a_100x100_block_fails_every_scene_including_night(self):
        # bright over the player's feet, black on lit ground (black on the unlit void is no change at all)
        for name in self.SCENES:
            for x, y, value in ((690, 440, 0.9), (400, 550, 0.0)):
                _, results = self.doctored(name, self.paint(x, y, 100, 100, value))
                self.assertFalse(results[-1][0], (name, x, y, value, results[-1]))

    def test_a_20x60_bar_on_the_noon_baseline_fails_where_it_is_painted(self):
        for x, y in ((500, 360), (240, 200), (600, 660), (1000, 500), (1100, 100)):
            _, results = self.doctored("spawn-noon", self.paint(x, y, 20, 60, 0.9))
            self.assertFalse(results[-1][0], (x, y, results[-1]))

    def test_a_200x300_block_fails_every_scene(self):
        for name in self.SCENES:
            _, results = self.doctored(name, self.paint(900, 80, 200, 300, 0.9))
            self.assertFalse(results[-1][0], (name, results[-1]))

    def test_recorded_noise_is_per_step_with_tone_and_blocks(self):
        for name in self.SCENES:
            noise = golden.json.loads((golden.BASELINES / name / "noise.json").read_text())
            self.assertGreaterEqual(noise["runs"], 8)
            steps = [p.name for p in golden.list_pngs(golden.BASELINES / name)]
            self.assertEqual(sorted(noise["perStep"]), steps)
            self.assertEqual(sorted(noise["tone"]), steps)
            self.assertEqual(noise["toneStats"], golden.TONE_NAMES)
            blocks = np.load(golden.BASELINES / name / "blocks.npz")
            self.assertEqual(sorted(blocks.files), steps)

    def test_wolf_ignore_rects_leave_the_world_and_the_player_in_the_compare(self):
        # the scene clears the wolf before the snap, so no damage number is masked any more: only the chat log and
        # the health orb are. The Gate Warden column (x 733..750, y 115..185), the damage-number zone and the
        # player (712..774, 480..571) must all be compared.
        scene = golden.load_scene("wolf-staged")
        mask = golden.ignore_mask((512, 720), scene["ignore"])
        self.assertFalse(mask[60:90, 368:376].any(), "Gate Warden must not be masked")
        self.assertFalse(mask[75:110, 325:366].any(), "damage-number zone must not be masked")
        self.assertFalse(mask[235:290, 350:395].any(), "the player must not be masked")
        self.assertTrue(mask[440:460, 10:150].all(), "chat log must stay masked")

    def test_wolf_removing_the_player_fails_with_at_least_twice_the_limit(self):
        _, results = self.doctored("wolf-staged", self.remove_figure())
        m = re.search(r"block ([\d.]+) ([\d.]+)", results[-1][1])
        self.assertIsNotNone(m, results[-1])
        self.assertGreaterEqual(float(m[1]), 2.0 * float(m[2]), results[-1])

    def test_wolf_scene_clears_the_wolf_before_the_snap(self):
        # a live wolf bites at a jittery real-time phase: its damage number (12 or 18) floats from the player's head
        # to the Gate Warden at a different height in every run; the frame must be taken after the wolf is gone
        script = golden.load_scene("wolf-staged")["script"].split(",")
        self.assertIn("dev:clear", script[script.index("status") + 1:], script)
        self.assertGreaterEqual(int(script[-2].split(":")[1]), 90, script)


if __name__ == "__main__":
    unittest.main()
