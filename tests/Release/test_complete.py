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

    @staticmethod
    def unityfs(engine):
        # UnityFS header: signature, format version, then the player and generator versions.
        return b'UnityFS\x00\x00\x00\x00\x08' + b'5.x.x\x00' + engine.encode() + b'\x00fixture'

    def components(self, output='complete', engines=None):
        # Each engine's component carries the bundle its own editor produced.
        components = {}
        for unity in self.targets:
            bundle = self.unityfs((engines or {}).get(unity, unity))
            components[unity] = self.build(self.files | {self.bundle: bundle}, suffix=f'{output}-{unity}')
        return components

    def combined(self, output='complete', engines=None):
        mods = self.components(output, engines)
        return implementation.build(mods, self.editors, self.fsr, self.root / output, self.commit, '0.6.1')

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
            self.assertEqual(implementation.component_engine(files), manifest['unityVersion'])

    def test_bundle_from_another_editor_cannot_ship_for_an_engine(self):
        # A newer editor's bundle in the legacy download is exactly the v0.6.2 packaging defect.
        with self.assertRaisesRegex(ValueError, 'not built with Unity 6000.4.1f1'):
            self.combined('newer', {'6000.4.1f1': '6000.5.8f1'})
        with self.assertRaisesRegex(ValueError, 'not built with Unity 6000.5.8f1'):
            self.combined('older', {'6000.5.8f1': '6000.4.1f1'})
        with self.assertRaisesRegex(ValueError, 'not a UnityFS archive'):
            implementation.build(self.components('plain') | {'6000.4.1f1': self.build(suffix='plain')},
                                 self.editors, self.fsr, self.root / 'plain', self.commit, '0.6.1')
        # One component cannot serve both engines.
        shared = self.components('shared')['6000.5.8f1']
        with self.assertRaisesRegex(ValueError, 'not built with Unity 6000.4.1f1'):
            implementation.build({unity: shared for unity in self.targets}, self.editors, self.fsr,
                                 self.root / 'shared', self.commit, '0.6.1')
        with self.assertRaisesRegex(ValueError, 'one mod component per supported engine'):
            implementation.build({'6000.5.8f1': shared}, self.editors, self.fsr, self.root / 'partial', self.commit, '0.6.1')
        # A fully rehashed download that swaps in another engine's bundle is still rejected.
        released = self.combined('swap')
        legacy = next(path for path in released if '0.2.8.5' in path.name)
        files = package.read_zip(legacy)
        bundle = next(name for name in files if name.endswith('.bundle'))
        files[bundle] = self.unityfs('6000.5.8f1')
        inner = json.loads(files.pop(package.PREFIX + 'package-manifest.json'))
        inner['files'] = [{'path': n[len(package.PREFIX):], 'bytes': len(d), 'sha256': package.digest(d)}
                          for n, d in sorted(files.items())
                          if n.startswith(package.PREFIX) and not n.startswith(implementation.runtimes.FSR_PREFIX)]
        files[package.PREFIX + 'package-manifest.json'] = package.encode_json(inner)
        manifest = json.loads(files.pop(implementation.MANIFEST))
        manifest['files'] = [{'path': n, 'bytes': len(d), 'sha256': package.digest(d)} for n, d in files.items()]
        files[implementation.MANIFEST] = package.encode_json(manifest)
        package.write_zip(self.root / 'swapped-bundle.zip', files)
        with self.assertRaisesRegex(ValueError, 'different Unity version'):
            implementation.verify(self.root / 'swapped-bundle.zip', self.commit, '0.6.1')

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
        components = self.components('collision')
        output = self.root / 'collision'
        output.mkdir()
        existing = output / 'ReduxBetterAA-0.6.1-Redux-0.2.8.5.zip'
        existing.write_bytes(b'preserve')
        with self.assertRaises(FileExistsError):
            implementation.build(components, self.editors, self.fsr, output, self.commit, '0.6.1')
        self.assertEqual(list(output.iterdir()), [existing])
        self.assertEqual(existing.read_bytes(), b'preserve')
