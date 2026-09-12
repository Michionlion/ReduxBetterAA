"""Release boundaries: malformed inputs, stray binaries and corrupted downloads."""
import importlib.util
import json
from pathlib import Path
import stat
import struct
import tempfile
import unittest
import warnings
import zipfile

SPEC = importlib.util.spec_from_file_location('packager', Path(__file__).parents[2] / 'tools/package-release.py')
package = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(package)


class PackagingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.commit = '1' * 40
        self.bundle = 'addressables/StandaloneWindows64/addressables_reduxbetteraa_all_assets_all_' + 'a' * 32 + '.bundle'
        # Minimal PE/version resource; no code is loaded during metadata validation.
        dll = bytearray(160)
        dll[:2] = b'MZ'
        struct.pack_into('<I', dll, 60, 64)
        dll[64:68] = b'PE\0\0'
        struct.pack_into('<4I', dll, 96, 0xFEEF04BD, 0x10000, 6, 1 << 16)
        info = {'version': '0.6.1', 'mod_id': 'ReduxBetterAA', 'main_assembly': 'ReduxBetterAA.dll'}
        catalog = {'m_InternalIds': [('{SpaceWarpPaths.ReduxBetterAA}/' + self.bundle).replace('/', '\\')] + [f'Assets/ReduxBetterAA/Shaders/{name}.shader' for name in sorted(package.SHADERS)]}
        self.files = {'ReduxBetterAA.dll': bytes(dll), 'swinfo.json': package.encode_json(info),
                      'addressables/catalog.json': package.encode_json(catalog), 'addressables/catalog.hash': b'0' * 32,
                      self.bundle: b'UnityFS test bundle'}
        for path in package.DOCS.values():
            file = self.root / path
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text('fixture')
        manifest = self.root / 'Assets/ReduxBetterAA/Copied/swinfo.json'
        manifest.parent.mkdir(parents=True)
        manifest.write_bytes(self.files['swinfo.json'])

    def build(self, files=None, suffix=''):
        sdk = self.root / f'sdk{suffix}.zip'
        package.write_zip(sdk, files if files is not None else self.files)
        return package.build(sdk, self.root / ('out' + suffix), self.commit, self.root)

    def test_build_removes_only_known_metadata_and_keeps_licenses(self):
        files = self.files | {name: b'build metadata' for name in package.BUILD_ONLY}
        mod = self.build(files)
        manifest = package.verify(mod, self.commit, '0.6.1')
        self.assertEqual(manifest['removedBuildMetadata'], sorted(package.BUILD_ONLY))
        self.assertEqual([name for name in package.read_zip(mod) if name.endswith('.dll')], [package.PREFIX + 'ReduxBetterAA.dll'])
        self.assertTrue(all(name.startswith(package.PREFIX) for name in package.read_zip(mod)))
        self.assertIn(package.PREFIX + 'LICENSE', package.read_zip(mod))

    def test_packaging_is_repeatable(self):
        a = self.build(suffix='a')
        b = self.build(suffix='b')
        self.assertEqual(a.read_bytes(), b.read_bytes())

    def test_game_test_and_native_binaries_are_rejected(self):
        for name in ('Assembly-CSharp.dll', 'ReduxBetterAA.Tests.dll', 'NVUnityPlugin.dll', 'secrets.txt'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                package.validate_payload(self.files | {name: b'forbidden'}, '0.6.1')

    def test_missing_or_different_bundle_fails(self):
        files = dict(self.files)
        del files[self.bundle]
        with self.assertRaises(ValueError):
            package.validate_payload(files, '0.6.1')
        files[self.bundle.replace('a' * 32, 'b' * 32)] = b'wrong'
        with self.assertRaisesRegex(ValueError, 'different bundle'):
            package.validate_payload(files, '0.6.1')

    def test_test_shader_cannot_enter_catalog(self):
        catalog = json.loads(self.files['addressables/catalog.json'])
        catalog['m_InternalIds'].append('Assets/Tests/Baseline.shader')
        with self.assertRaisesRegex(ValueError, 'nine runtime shaders'):
            package.validate_payload(self.files | {'addressables/catalog.json': package.encode_json(catalog)}, '0.6.1')

    def test_assembly_version_mismatch_fails(self):
        dll = bytearray(self.files['ReduxBetterAA.dll'])
        struct.pack_into('<I', dll, 108, 0)
        with self.assertRaisesRegex(ValueError, 'versions disagree'):
            package.validate_payload(self.files | {'ReduxBetterAA.dll': bytes(dll)}, '0.6.1')

    def test_unsafe_archive_paths_and_links_fail(self):
        for index, name in enumerate(('../evil', '/evil', 'C:/evil', 'a\\evil', 'a//evil', 'a/../evil', 'a./evil')):
            path = self.root / f'bad{index}.zip'
            with zipfile.ZipFile(path, 'w') as archive:
                entry = zipfile.ZipInfo('placeholder')
                entry.filename = name  # Bypass Python's Windows separator normalization.
                archive.writestr(entry, b'bad')
            with self.subTest(name=name), self.assertRaises(ValueError):
                package.read_zip(path)
        path = self.root / 'link.zip'
        entry = zipfile.ZipInfo('link')
        entry.external_attr = (stat.S_IFLNK | 0o777) << 16
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr(entry, '../target')
        with self.assertRaisesRegex(ValueError, 'symlink'):
            package.read_zip(path)

    def test_windows_case_collisions_fail(self):
        path = self.root / 'duplicate.zip'
        with zipfile.ZipFile(path, 'w') as archive, warnings.catch_warnings():
            warnings.simplefilter('ignore')
            archive.writestr('swinfo.json', b'a')
            archive.writestr('SWINFO.JSON', b'b')
        with self.assertRaisesRegex(ValueError, 'Duplicate'):
            package.read_zip(path)

    def test_download_tampering_and_wrong_commit_fail(self):
        mod = self.build()
        with self.assertRaisesRegex(ValueError, 'provenance'):
            package.verify(mod, '2' * 40, '0.6.1')
        files = package.read_zip(mod)
        files[package.PREFIX + self.bundle] += b'changed'
        corrupted = self.root / 'corrupted.zip'
        package.write_zip(corrupted, files)
        with self.assertRaisesRegex(ValueError, 'hash mismatch'):
            package.verify(corrupted, self.commit, '0.6.1')

    def test_license_omission_fails(self):
        (self.root / 'LICENSE').unlink()
        with self.assertRaises(FileNotFoundError):
            self.build()


if __name__ == '__main__':
    unittest.main()
