import hashlib
import importlib.util
import json
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
            names = {'NVUnityPlugin.dll', 'nvngx_dlss.dll'}
            hashes = {}
            for name in names:
                data = name.encode()
                (player / name).write_bytes(data)
                hashes[name] = hashlib.sha256(data).hexdigest()
            (player / 'unrelated.pdb').write_bytes(b'exclude me')
            (player / 'AMDUnityPlugin.dll').write_bytes(b'legacy runtime must not ship')
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

    def test_pinned_targets_require_only_the_two_nvidia_libraries(self):
        for target in runtimes.TARGETS.values():
            self.assertEqual(set(target['hashes']), {'NVUnityPlugin.dll', 'nvngx_dlss.dll'})
            self.assertTrue(all(len(digest) == 64 for digest in target['hashes'].values()))

    def test_missing_legacy_amd_library_does_not_block_packaging(self):
        with tempfile.TemporaryDirectory() as directory:
            editor = Path(directory) / '6000.5.8f1/Editor'
            player = editor / runtimes.PLAYER
            player.mkdir(parents=True)
            hashes = {}
            for name in runtimes.RUNTIME_DLLS:
                data = name.encode()
                (player / name).write_bytes(data)
                hashes[name] = hashlib.sha256(data).hexdigest()
            with patch.object(runtimes, 'TARGETS', {'6000.5.8f1': {'redux': '0.2.9.0', 'hashes': hashes}}):
                payload = next(iter(runtimes.collect([editor]).values()))
                self.assertEqual(set(payload), runtimes.RUNTIME_DLLS | {runtimes.NOTICE})

    def test_manifest_cannot_reintroduce_legacy_or_unapproved_runtime(self):
        for unexpected in ('AMDUnityPlugin.dll', 'amd_fidelityfx_upscaler_dx12.dll', 'other.dll'):
            hashes = {name: '0' * 64 for name in runtimes.RUNTIME_DLLS | {unexpected}}
            with self.subTest(unexpected=unexpected), patch.object(runtimes, 'TARGETS', {'6000.5.8f1': {'hashes': hashes}}):
                with self.assertRaisesRegex(ValueError, 'exactly the two NVIDIA'):
                    runtimes.collect([])

    def test_package_rejects_wrong_payload_before_creating_any_archive(self):
        valid = {name: b'test' for name in runtimes.RUNTIME_DLLS | {runtimes.NOTICE}}
        for invalid in (dict(valid, **{'AMDUnityPlugin.dll': b'legacy'}),
                        {name: data for name, data in valid.items() if name != 'nvngx_dlss.dll'},
                        {name: data for name, data in valid.items() if name != runtimes.NOTICE}):
            with self.subTest(names=sorted(invalid)), tempfile.TemporaryDirectory() as directory:
                output = Path(directory) / 'output'
                with self.assertRaisesRegex(ValueError, 'Unexpected or missing NVIDIA'):
                    runtimes.package({'valid.zip': valid, 'invalid.zip': invalid}, output)
                self.assertFalse(output.exists())

    def test_missing_or_duplicate_editor_fails(self):
        with self.assertRaises(ValueError):
            runtimes.collect([])
        with self.assertRaises(ValueError):
            runtimes.collect(['6000.5.8f1/Editor'] * 2)

    def test_modern_fsr_archive_requires_exact_files_pinned_vendor_and_matching_version(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'Native').mkdir()
            payload = {'ReduxBetterAA.FsrBridge.dll': b'MZ project bridge',
                       'amd_fidelityfx_upscaler_dx12.dll': b'MZ pinned AMD vendor',
                       'THIRD-PARTY-NOTICES-FSR.txt': b'AMD license and notices; commit abc123'}
            hashes = {name: hashlib.sha256(data).hexdigest() for name, data in payload.items() if name.endswith('.dll')}
            pinned = {'schemaVersion': 1, 'abiVersion': 1, 'sdk': {'commit': 'abc123', 'version': '2.3.0'},
                      'graphicsApi': 'D3D11 with same-adapter D3D12 interop',
                      'files': [{'filename': 'amd_fidelityfx_upscaler_dx12.dll', 'sha256': hashes['amd_fidelityfx_upscaler_dx12.dll']}]}
            (root / 'Native/runtime-manifest.json').write_text(json.dumps(pinned))
            manifest = pinned | {'modVersion': '0.6.2', 'files': [{'filename': name, 'sha256': digest} for name, digest in hashes.items()]}
            archive = root / 'fsr.zip'
            def write(data, current_manifest=manifest):
                with zipfile.ZipFile(archive, 'w') as zipped:
                    for name, content in data.items():
                        zipped.writestr(runtimes.FSR_PREFIX + name, content)
                    zipped.writestr(runtimes.FSR_PREFIX + 'fsr-runtime-manifest.json', json.dumps(current_manifest))
            with patch.object(runtimes, 'ROOT', root):
                write(payload)
                validated = runtimes.validate_fsr_archive(archive, '0.6.2')
                self.assertEqual(validated['files'], hashes)
                with self.assertRaisesRegex(ValueError, 'different Better AA version'):
                    runtimes.validate_fsr_archive(archive, '0.6.3')
                write(payload | {'AMDUnityPlugin.dll': b'legacy'})
                with self.assertRaisesRegex(ValueError, 'archive files'):
                    runtimes.validate_fsr_archive(archive, '0.6.2')
                write(payload | {'ReduxBetterAA.FsrBridge.dll': b'corrupt'})
                with self.assertRaisesRegex(ValueError, 'hash mismatch'):
                    runtimes.validate_fsr_archive(archive, '0.6.2')
                write(payload, manifest | {'abiVersion': 2})
                with self.assertRaisesRegex(ValueError, 'pinned SDK or bridge ABI'):
                    runtimes.validate_fsr_archive(archive, '0.6.2')
                replaced = payload | {'amd_fidelityfx_upscaler_dx12.dll': b'MZ unapproved vendor'}
                forged = json.loads(json.dumps(manifest))
                for entry in forged['files']:
                    if entry['filename'] == 'amd_fidelityfx_upscaler_dx12.dll':
                        entry['sha256'] = hashlib.sha256(replaced[entry['filename']]).hexdigest()
                write(replaced, forged)
                with self.assertRaisesRegex(ValueError, 'pinned hash'):
                    runtimes.validate_fsr_archive(archive, '0.6.2')
                write(payload | {'THIRD-PARTY-NOTICES-FSR.txt': b'missing'})
                with self.assertRaisesRegex(ValueError, 'license and SDK notices'):
                    runtimes.validate_fsr_archive(archive, '0.6.2')
