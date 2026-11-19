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

    def record(self, frame=None, scene=None, noisy_b=None):
        a, b = self.root / "ra", self.root / "rb"
        frame = golden.synthetic_frame() if frame is None else frame
        save(a / "s1.png", frame)
        save(b / "s1.png", frame if noisy_b is None else noisy_b)
        return golden.record(a, b, scene or self.scene, self.base)

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

    def test_effective_limit_is_max_of_floor_and_three_times_noise(self):
        self.assertEqual(golden.effective_limit(0.002, 0.0), 0.002)
        self.assertEqual(golden.effective_limit(0.002, 0.0005), 0.002)
        self.assertAlmostEqual(golden.effective_limit(0.002, 0.01), 0.03)

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
        loud[10:30, 10:60] = 255  # the same change as the noise: within 3x noise
        self.assertTrue(all(ok for ok, _ in self.run_compare(loud, name="loud")))
        # Same recording but noise-free A/B: the same change now exceeds the floor and fails.
        self.record(frame=a)
        self.assertFalse(all(ok for ok, _ in self.run_compare(loud, name="loud2")))

    def test_selftest_passes(self):
        self.assertEqual(golden.selftest(), 0)


if __name__ == "__main__":
    unittest.main()
