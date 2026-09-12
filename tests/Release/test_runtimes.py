import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('runtimes', ROOT / 'tools/package-runtimes.py')
runtimes = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtimes)


class RuntimeTests(unittest.TestCase):
    def test_exact_payload_and_reject_changed_source(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            editor = root / '6000.5.8f1/Editor'
            player = editor / runtimes.PLAYER
            player.mkdir(parents=True)
            names = {'AMDUnityPlugin.dll', 'NVUnityPlugin.dll', 'nvngx_dlss.dll'}
            hashes = {}
            for name in names:
                data = name.encode()
                (player / name).write_bytes(data)
                hashes[name] = hashlib.sha256(data).hexdigest()
            (player / 'unrelated.pdb').write_bytes(b'exclude me')
            with patch.object(runtimes, 'TARGETS', {'6000.5.8f1': {'redux': '0.2.9.0', 'hashes': hashes}}):
                payloads = runtimes.collect([editor])
                runtimes.package(payloads, root / 'output')
                archive = next((root / 'output').glob('*.zip'))
                with zipfile.ZipFile(archive) as zipped:
                    self.assertEqual(set(zipped.namelist()), names | {'BetterAA-Runtime-Notices.txt'})
                    for name in names:
                        self.assertEqual(zipped.read(name), (player / name).read_bytes())
                original = archive.read_bytes()
                with self.assertRaises(FileExistsError):
                    runtimes.package(payloads, root / 'output')
                self.assertEqual(original, archive.read_bytes())
                (player / 'NVUnityPlugin.dll').write_bytes(b'wrong version')
                with self.assertRaisesRegex(ValueError, 'Unexpected'):
                    runtimes.collect([editor])

    def test_missing_or_duplicate_editor_fails(self):
        with self.assertRaises(ValueError):
            runtimes.collect([])
        with self.assertRaises(ValueError):
            runtimes.collect(['6000.5.8f1/Editor'] * 2)
