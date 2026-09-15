"""Exercise the actual single-download build and its trust boundaries."""
import json
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch
import test_packaging
package = test_packaging.package

complete = importlib.util.spec_from_file_location('complete', Path(__file__).parents[2] / 'tools/package-complete.py')
implementation = importlib.util.module_from_spec(complete)
complete.loader.exec_module(implementation)


class CompleteTests(unittest.TestCase):
    build = test_packaging.PackagingTests.build

    def setUp(self):
        test_packaging.PackagingTests.setUp(self)
        self.editors = []
        self.targets = {}
        for unity, redux in [('6000.5.8f1', '0.2.9.0'), ('6000.4.1f1', '0.2.8.5')]:
            editor = self.root / unity / 'Editor'
            player = editor / implementation.runtimes.PLAYER
            player.mkdir(parents=True)
            hashes = {}
            for name in implementation.runtimes.RUNTIME_DLLS:
                data = (unity + name).encode()
                (player / name).write_bytes(data)
                hashes[name] = package.digest(data)
            self.editors.append(editor)
            self.targets[unity] = {'redux': redux, 'hashes': hashes}
        (self.root / 'licenses/Native-Runtimes.txt').write_text('fixture notices')
        fsr = {'ReduxBetterAA.FsrBridge.dll': b'bridge', 'amd_fidelityfx_upscaler_dx12.dll': b'AMD vendor',
               'THIRD-PARTY-NOTICES-FSR.txt': b'AMD license abc123'}
        pinned = {'sdk': {'commit': 'abc123'}, 'abiVersion': 1, 'graphicsApi': 'D3D11',
                  'files': [{'filename': 'amd_fidelityfx_upscaler_dx12.dll', 'sha256': package.digest(b'AMD vendor')}]}
        (self.root / 'Native').mkdir()
        (self.root / 'Native/runtime-manifest.json').write_text(json.dumps(pinned))
        manifest = pinned | {'schemaVersion': 1, 'modVersion': '0.6.1',
                             'files': [{'filename': n, 'sha256': package.digest(d)} for n, d in fsr.items() if n.endswith('.dll')]}
        fsr['fsr-runtime-manifest.json'] = package.encode_json(manifest)
        self.fsr = self.root / 'fsr.zip'
        package.write_zip(self.fsr, {implementation.runtimes.FSR_PREFIX + n: d for n, d in fsr.items()})
        for obj, name, value in [(implementation, 'ROOT', self.root), (implementation.runtimes, 'ROOT', self.root),
                                 (implementation.runtimes, 'TARGETS', self.targets)]:
            patcher = patch.object(obj, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def combined(self, output='complete'):
        mod = self.build(suffix=output)
        return implementation.build(mod, self.editors, self.fsr, self.root / output, self.commit, '0.6.1')

    def test_complete_downloads_are_engine_specific_and_repeatable(self):
        first, second = self.combined(), self.combined('repeat')
        self.assertEqual(len(first), 2)
        for a, b in zip(first, second):
            self.assertEqual(a.read_bytes(), b.read_bytes())
            manifest = implementation.verify(a, self.commit, '0.6.1')
            self.assertTrue(manifest['includesNativeLibraries'])
            files = package.read_zip(a)
            self.assertIn(package.PREFIX + 'ReduxBetterAA.dll', files)
            self.assertIn(implementation.runtimes.FSR_PREFIX + 'ReduxBetterAA.FsrBridge.dll', files)
            self.assertIn(manifest['reduxVersion'], a.name)

    def test_swapped_engine_dll_and_unexpected_payload_fail_even_if_rehashed(self):
        original = self.combined()[0]
        for name in ('NVUnityPlugin.dll', 'mods/ReduxBetterAA/FrameGeneration.dll', '../escape.dll'):
            files = package.read_zip(original)
            files[name] = b'wrong binary'
            manifest = json.loads(files.pop(implementation.MANIFEST))
            manifest['files'] = [{'path': n, 'bytes': len(d), 'sha256': package.digest(d)} for n, d in files.items()]
            files[implementation.MANIFEST] = package.encode_json(manifest)
            altered = self.root / ('altered-' + str(len(list(self.root.glob('altered-*')))) + '.zip')
            package.write_zip(altered, files)
            with self.assertRaises(ValueError):
                implementation.verify(altered, self.commit, '0.6.1')

    def test_all_output_collisions_checked_before_any_write(self):
        component = self.build()
        output = self.root / 'collision'
        output.mkdir()
        existing = output / 'ReduxBetterAA-0.6.1-Redux-0.2.8.5.zip'
        existing.write_bytes(b'preserve')
        with self.assertRaises(FileExistsError):
            implementation.build(component, self.editors, self.fsr, output, self.commit, '0.6.1')
        self.assertEqual(list(output.iterdir()), [existing])
        self.assertEqual(existing.read_bytes(), b'preserve')
