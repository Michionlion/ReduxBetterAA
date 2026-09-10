"""Small independent fixtures for raw layout, numeric metrics and invalid captures."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
import numpy as np

spec = importlib.util.spec_from_file_location('analysis', Path(__file__).resolve().parents[2] / 'tools/analyze-aa-quality.py')
analysis = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analysis)


class AnalysisTests(unittest.TestCase):
    def test_known_pixel_errors(self):
        a = np.zeros((2, 2, 3), dtype=np.float32)
        b = a.copy(); b[0, 0] = 1
        result = analysis.metrics(a, b)
        self.assertEqual(result['mae'], .25)
        self.assertEqual(result['rmse'], .5)
        self.assertEqual(result['changed_fraction'], .25)
        self.assertAlmostEqual(result['psnr_peak1_db'], 6.020599913)

    def test_identity_psnr_is_not_infinite_json(self):
        x = np.ones((2, 2, 3), dtype=np.float32)
        self.assertEqual(analysis.metrics(x, x)['mae'], 0)
        self.assertIsNone(analysis.metrics(x, x)['psnr_peak1_db'])

    def test_raw_bottom_row_layout_and_hdr_preservation(self):
        with tempfile.TemporaryDirectory() as tmp:
            pixels = np.zeros((2, 2, 4), dtype='<f2'); pixels[0, :, 0] = 4
            pixels.tofile(Path(tmp) / 'test.raw')
            x = analysis.load(Path(tmp), dict(width=2, height=2, output='test.raw'), 'output')
            np.testing.assert_equal(x[1, :, 0], 4)
            np.testing.assert_equal(x[0, :, 0], 0)

    def test_rejects_truncation_and_nonfinite_alpha(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'test.raw'
            f = dict(width=2, height=2, output='test.raw')
            path.write_bytes(b'\x00\x00')
            with self.assertRaisesRegex(ValueError, 'byte count'):
                analysis.load(Path(tmp), f, 'output')
            pixels = np.zeros((2, 2, 4), dtype='<f2'); pixels[0, 0, 3] = np.nan
            pixels.tofile(path)
            with self.assertRaisesRegex(ValueError, 'Nonfinite'):
                analysis.load(Path(tmp), f, 'output')


if __name__ == '__main__':
    unittest.main()
