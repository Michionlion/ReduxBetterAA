"""CPU-only companion boundaries; PE fixtures contain metadata and no executable code."""
import copy
import importlib.util
import json
from pathlib import Path
import stat
import struct
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SPEC = importlib.util.spec_from_file_location('fg_package', Path(__file__).parents[1] / 'package-frame-generation.py')
package = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(package)


def dll(exports, machine=0x8664):
    data = bytearray(4096)
    data[:2] = b'MZ'
    struct.pack_into('<I', data, 60, 64)
    data[64:68] = b'PE\0\0'
    struct.pack_into('<HH', data, 68, machine, 1)
    struct.pack_into('<HH', data, 84, 240, 0x2000)
    struct.pack_into('<H', data, 88, 0x20b)
    struct.pack_into('<II', data, 200, 0x1000, 256)
    struct.pack_into('<IIII', data, 336, 3584, 0x1000, 3584, 512)
    names = sorted(exports)
    struct.pack_into('<I', data, 512 + 24, len(names))
    struct.pack_into('<I', data, 512 + 32, 0x1040)
    cursor = 700
    for index, name in enumerate(names):
        struct.pack_into('<I', data, 576 + index * 4, 0x1000 + cursor - 512)
        raw = name.encode() + b'\0'
        data[cursor:cursor + len(raw)] = raw
        cursor += len(raw)
    return bytes(data)


class FrameGenerationPackagingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name)
        self.root = self.path / 'source'
        self.root.mkdir()
        self.pins = copy.deepcopy(package.PINS)
        self.addCleanup(patch.stopall)
        patch.object(package, 'PINS', self.pins).start()
        self.source_check = patch.object(package, 'validate_source_pins').start()
        self.git_check = patch.object(package, 'check_git').start()
        self.unity = self.path / 'unity'
        self.runtimes = {name: self.path / name for name in self.pins['vendors']}
        self.providers = {name: self.path / pin['provider'] for name, pin in self.pins['vendors'].items()}
        self.coordinator = self.path / 'coordinator.dll'
        self.coordinator.write_bytes(dll(package.EXPORTS['coordinator']))
        self.write(self.root / 'LICENSE', b'Test MIT license')
        self.write(self.root / 'tools/licenses/Unity-Companion-License.txt', b'Unity Companion License complete test fixture')
        self.pins['betterAaLicenseSha256'] = package.sha((self.root / 'LICENSE').read_bytes())
        self.pins['unityCompanionLicenseSha256'] = package.sha((self.root / 'tools/licenses/Unity-Companion-License.txt').read_bytes())
        self.write(self.root / 'Assets/ReduxBetterAA/Copied/swinfo.json', b'{"version":"0.6.4"}')
        for pin in self.pins['unityFiles']:
            self.fixture_pin(self.unity, pin)
        for name, vendor in self.pins['vendors'].items():
            self.providers[name].write_bytes(dll(package.EXPORTS[name]))
            for pin in vendor['runtimeFiles'] + vendor['licenseFiles']:
                self.fixture_pin(self.runtimes[name], pin)

    def write(self, path, data):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def fixture_pin(self, root, pin):
        data = ('Pinned test input: ' + pin['source']).encode()
        self.write(root / pin['source'], data)
        pin['sha256'] = package.sha(data)

    def build(self, vendors=('nvidia', 'amd'), suffix='a'):
        return package.package(self.path / f'fg-{suffix}.zip', self.coordinator, self.unity, list(vendors),
                               self.providers, self.runtimes, self.runtimes, root=self.root)

    def rewrite(self, path, transform):
        with zipfile.ZipFile(path) as archive:
            files = {e.orig_filename: archive.read(e) for e in archive.infolist()}
        transform(files)
        with zipfile.ZipFile(path, 'w') as archive:
            for name, data in files.items():
                archive.writestr(name, data)

    def test_deterministic_both_vendor_companion_and_abi2(self):
        first, second = self.build(suffix='a'), self.build(suffix='b')
        self.assertEqual(first.read_bytes(), second.read_bytes())
        manifest = package.verify(first, '0.6.4')
        self.assertEqual(manifest['coordinatorAbi'], 2)
        self.assertEqual(manifest['vendors'], ['amd', 'nvidia'])
        self.assertEqual(self.git_check.call_count, 4)
        with zipfile.ZipFile(first) as archive:
            self.assertEqual(set(archive.namelist()), package.expected_paths(['amd', 'nvidia']) | {package.MANIFEST})
            self.assertNotIn('mods/ReduxBetterAA/ReduxBetterAA.dll', archive.namelist())
            self.assertTrue(all('TestRuntime' not in n and 'StreamlineProbe' not in n for n in archive.namelist()))

    def test_each_vendor_is_optional_without_other_vendor_inputs(self):
        for name in ['nvidia', 'amd']:
            with self.subTest(name=name):
                archive = self.build((name,), name)
                manifest = package.verify(archive, '0.6.4')
                self.assertEqual(manifest['vendors'], [name])
                absent = self.pins['vendors']['amd' if name == 'nvidia' else 'nvidia']['provider']
                self.assertFalse(any(e['path'].endswith(absent) for e in manifest['files']))

    def test_runtime_and_license_mutations_reject_before_archive_creation(self):
        for field in ['runtimeFiles', 'licenseFiles']:
            pin = self.pins['vendors']['nvidia'][field][0]
            path = self.runtimes['nvidia'] / pin['source']
            original = path.read_bytes()
            path.write_bytes(original + b'changed')
            with self.assertRaisesRegex(ValueError, 'Pinned file mismatch'):
                self.build(('nvidia',), field)
            self.assertFalse((self.path / f'fg-{field}.zip').exists())
            path.write_bytes(original)

    def test_wrong_architecture_and_abi1_coordinator_reject(self):
        for exports, machine in [(package.EXPORTS['coordinator'], 0x14c), ({'UnityPluginLoad', 'RbaFgConfigure'}, 0x8664)]:
            self.coordinator.write_bytes(dll(exports, machine))
            with self.assertRaises(ValueError):
                self.build()

    def test_vendor_hash_cannot_be_laundered_by_rewriting_manifest(self):
        archive = self.build(('amd',))

        def mutate(files):
            target = package.PREFIX + 'amd/amd_fidelityfx_framegeneration_dx12.dll'
            files[target] += b'corrupted'
            manifest = json.loads(files[package.MANIFEST])
            for entry in manifest['files']:
                if entry['path'] == target:
                    entry['bytes'] = len(files[target])
                    entry['sha256'] = package.sha(files[target])
            files[package.MANIFEST] = package.encoded(manifest)

        self.rewrite(archive, mutate)
        with self.assertRaisesRegex(ValueError, 'vendor runtime pin mismatch'):
            package.verify(archive, '0.6.4')

    def test_wrong_version_or_missing_notice_rejects(self):
        archive = self.build()
        with self.assertRaisesRegex(ValueError, 'version/ABI'):
            package.verify(archive, '0.6.5')
        self.rewrite(archive, lambda files: files.pop(package.PREFIX + 'licenses/nvidia/license.txt'))
        with self.assertRaisesRegex(ValueError, 'Unexpected/missing'):
            package.verify(archive, '0.6.4')

    def test_unsafe_extra_duplicate_case_or_symlink_rejects(self):
        for index, (name, link) in enumerate([('../escape', False), (package.COORDINATOR.upper(), False), ('extra.dll', True)]):
            archive = self.build(suffix=f'unsafe-{index}')
            with zipfile.ZipFile(archive, 'a') as zipped:
                entry = zipfile.ZipInfo(name)
                if link:
                    entry.external_attr = (stat.S_IFLNK | 0o777) << 16
                zipped.writestr(entry, b'bad')
            with self.assertRaises(ValueError):
                package.verify(archive, '0.6.4')

    def test_source_output_and_existing_archive_refused(self):
        with self.assertRaisesRegex(ValueError, 'outside source'):
            package.outside(self.root / 'output.zip', self.root)
        self.build()
        with self.assertRaisesRegex(ValueError, 'already exists'):
            self.build()


class RealSourcePinTests(unittest.TestCase):
    def test_package_pins_agree_with_native_loaders_and_unity_headers(self):
        package.validate_source_pins()


if __name__ == '__main__':
    unittest.main()
