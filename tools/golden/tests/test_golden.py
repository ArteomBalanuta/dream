"""Golden compare tests. Run: python -I tools/golden/tests/test_golden.py"""
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
        self.assertEqual(k, 4.0)
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

    def test_selftest_passes(self):
        self.assertEqual(golden.selftest(), 0)


class RealBaselineTests(unittest.TestCase):
    """The committed baselines, with a doctored frame: the gate must still be able to fail on real pixels."""

    BAR = (20, 60)  # full-resolution px, the "moved bar" class: a sword at the sky, a trail above the head

    def doctored(self, scene_name, x=0, y=0, value=0.9, paint=True, bar=None):
        scene = golden.load_scene(scene_name)
        base_dir = golden.BASELINES / scene_name
        baseline = golden.list_pngs(base_dir)
        self.assertTrue(baseline, f"no committed baseline for {scene_name}: record with tools/golden/run.ps1 -Record")
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        actual = Path(tmp.name) / "actual"
        for path in baseline:
            # the baseline is stored at half resolution: upscale it to a capture-sized frame
            half = golden.read_rgb(path)
            full = np.repeat(np.repeat(half, 2, axis=0), 2, axis=1)
            if paint and path == baseline[-1]:
                w, h = bar or self.BAR
                full[y:y + h, x:x + w] = value
            save(actual / path.name, (full * 255.0 + 0.5).astype(np.uint8))
        return scene, golden.compare(actual, scene, golden.BASELINES, Path(tmp.name) / "out")

    def test_undoctored_baseline_passes_itself(self):
        for name in ("spawn-noon", "spawn-night", "wolf-staged"):
            _, results = self.doctored(name, paint=False)
            self.assertTrue(all(ok for ok, _ in results), results)

    def test_a_20x60_bar_on_the_noon_baseline_fails_everywhere_it_is_painted(self):
        for x, y in ((500, 360), (240, 200), (600, 660), (1000, 500)):
            _, results = self.doctored("spawn-noon", x, y)
            self.assertFalse(results[-1][0], (x, y, results[-1]))

    def test_a_20x60_bar_in_the_dark_fails_the_noon_scene(self):
        # Upper-right of the frame: sky/void, unlit by the torch, so flicker cannot hide it.
        _, results = self.doctored("spawn-noon", 1100, 100)
        self.assertFalse(results[-1][0], results[-1])

    def test_night_and_wolf_are_gross_change_gates(self):
        # Their limits are 4x the measured noise (night: torch flicker over the lit ground, about 1% of the frame;
        # wolf: the fight), so a 20x60 bar (0.08%) passes there by design; a 200x300 block (4%) must still fail.
        for name in ("spawn-night", "wolf-staged"):
            _, small = self.doctored(name, 1100, 100)
            self.assertTrue(small[-1][0], (name, small[-1]))
            _, big = self.doctored(name, 900, 80, bar=(200, 300))
            self.assertFalse(big[-1][0], (name, big[-1]))


if __name__ == "__main__":
    unittest.main()
