import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('all_runtimes', ROOT / 'tools/package-all-runtimes.py')
combined = importlib.util.module_from_spec(spec)
spec.loader.exec_module(combined)


class CombinedRuntimeTests(unittest.TestCase):
    def payloads(self):
        nvidia = {f"BetterAA-Runtimes-Redux-{t['redux']}.zip": {'NVUnityPlugin.dll': u.encode()}
                  for u, t in combined.runtimes.TARGETS.items()}
        return combined.assemble(nvidia, {'mods/fsr.dll': b'fsr'}, {'plugins/fg.dll': b'fg'}, '0.6.2')

    def test_engine_boundary_and_complete_manifest(self):
        for name, payload in self.payloads().items():
            manifest = json.loads(payload[combined.MANIFEST])
            self.assertIn('mods/fsr.dll', payload)
            self.assertEqual('plugins/fg.dll' in payload, manifest['unityVersion'] == combined.fg.PINS['unityVersion'])
            self.assertEqual({f['path'] for f in manifest['files']}, set(payload) - {combined.MANIFEST})

    def test_deterministic_archives_and_no_overwrite(self):
        with tempfile.TemporaryDirectory() as directory:
            a, b = Path(directory)/'a', Path(directory)/'b'
            combined.package(self.payloads(), a)
            combined.package(self.payloads(), b)
            for path in a.glob('*.zip'):
                self.assertEqual(path.read_bytes(), (b/path.name).read_bytes())
                with zipfile.ZipFile(path) as archive:
                    self.assertEqual(set(archive.namelist()), set(self.payloads()[path.name]))
            with self.assertRaises(FileExistsError):
                combined.package(self.payloads(), a)

    def test_component_collision_rejected(self):
        with self.assertRaises(ValueError):
            combined.merge({'same.dll': b'one'}, {'same.dll': b'two'})
