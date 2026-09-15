"""Build and verify the public ZIPs. No game files or native DLLs are copied."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import stat
import struct
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PREFIX = 'mods/ReduxBetterAA/'
REQUIRED = {'ReduxBetterAA.dll', 'swinfo.json', 'addressables/catalog.json', 'addressables/catalog.hash'}
BUILD_ONLY = {'addressables/settings.json', 'addressables/AddressablesLink/link.xml'}
DOCS = {
    'INSTALL.md': 'docs/INSTALL.md', 'NATIVES.md': 'NATIVES.md',
    'LICENSE': 'LICENSE', 'THIRD-PARTY-NOTICES.md': 'THIRD-PARTY-NOTICES.md',
    'licenses/Unity-Built-in-Shaders.txt': 'licenses/Unity-Built-in-Shaders.txt',
}
SHADERS = {
    'AaComparison', 'FsrBridgeInputs', 'CustomTaa', 'DepthDisocclusionMask', 'IssueBufferCapture',
    'MotionVectorSanitizer', 'Phase1BufferDebug', 'Phase1MotionStatistics',
    'Phase1MotionVectorPassProbe', 'VegetationMotionVectorRepair',
}
SEMVER = r'(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)'
BUNDLE = r'addressables/StandaloneWindows64/addressables_reduxbetteraa_all_assets_all_[0-9a-f]{32}\.bundle'


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read_zip(path, limit=16 * 1024 * 1024):
    result = {}
    seen = set()
    with zipfile.ZipFile(path) as archive:
        require(sum(e.file_size for e in archive.infolist()) < limit, 'Archive exceeds the payload limit')
        for entry in archive.infolist():
            # ZipInfo normalizes backslashes on Windows; validate the stored spelling first.
            name = entry.orig_filename
            require(not entry.is_dir(), 'Directory entries are not part of the package contract')
            require(not name.startswith('/') and not re.search(r'[\\:\x00-\x1f]', name)
                    and all(p not in ('', '.', '..') and not p.endswith(('.', ' ')) for p in name.split('/')),
                    f'Unsafe archive path: {name}')
            require(name.casefold() not in seen, f'Duplicate archive path: {name}')
            require(not stat.S_ISLNK(entry.external_attr >> 16), f'Archive symlink: {name}')
            seen.add(name.casefold())
            result[name] = archive.read(entry)
    return result


def dll_version(data):
    require(data[:2] == b'MZ' and len(data) > 64, 'Mod assembly is not a PE file')
    pe = struct.unpack_from('<I', data, 60)[0]
    require(data[pe:pe + 4] == b'PE\0\0', 'Mod assembly has no PE header')
    # VS_FIXEDFILEINFO stores the same version reported by Windows Explorer.
    signature = struct.pack('<II', 0xFEEF04BD, 0x00010000)
    positions = [m.start() for m in re.finditer(re.escape(signature), data)]
    require(len(positions) == 1, 'Expected one Windows file-version resource')
    ms, ls = struct.unpack_from('<II', data, positions[0] + 8)
    return f'{ms >> 16}.{ms & 65535}.{ls >> 16}.{ls & 65535}'


def validate_payload(files, version):
    require(re.fullmatch(SEMVER, version), 'Expected a numeric major.minor.patch version')
    bundles = [name for name in files if re.fullmatch(BUNDLE, name)]
    require(len(bundles) == 1, 'Expected exactly one Better AA Windows bundle')
    require(set(files) == REQUIRED | set(bundles), 'Unexpected or missing runtime payload')
    info = json.loads(files['swinfo.json'])
    require(info['version'] == version and info['mod_id'] == 'ReduxBetterAA'
            and info['main_assembly'] == 'ReduxBetterAA.dll', 'Mod identity/version mismatch')
    require(dll_version(files['ReduxBetterAA.dll']) == version + '.0', 'Assembly and manifest versions disagree')
    catalog = json.loads(files['addressables/catalog.json'])
    ids = catalog['m_InternalIds']
    shaders = {f'Assets/ReduxBetterAA/Shaders/{name}.shader' for name in SHADERS}
    bundle_ids = [item for item in ids if item.endswith('.bundle')]
    require(len(bundle_ids) == 1 and bundle_ids[0].replace('\\', '/') == '{SpaceWarpPaths.ReduxBetterAA}/' + bundles[0],
            'Catalog refers to a different bundle')
    require(set(ids) == shaders | set(bundle_ids), 'Catalog must contain only the nine runtime shaders and bundle')
    require(re.fullmatch(rb'[0-9a-fA-F]{32}\s*', files['addressables/catalog.hash']), 'Invalid catalog hash')


def write_zip(path, files):
    require(not path.exists(), f'Output already exists: {path}')
    path.parent.mkdir(parents=True, exist_ok=True)
    # Stable metadata makes packaging identical for identical inputs.
    with zipfile.ZipFile(path, 'x', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(files.items()):
            entry = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, data)


def encode_json(data):
    return (json.dumps(data, indent=2) + '\n').encode()


def build(sdk, output, commit, root=ROOT):
    require(re.fullmatch(r'[0-9a-f]{40}', commit), 'Expected a full Git commit hash')
    version = json.loads((root / 'Assets/ReduxBetterAA/Copied/swinfo.json').read_text())['version']
    sdk_files = read_zip(sdk)
    removed = sorted(set(sdk_files) & BUILD_ONLY)
    payload = {name: data for name, data in sdk_files.items() if name not in BUILD_ONLY}
    validate_payload(payload, version)
    require(payload['swinfo.json'] == (root / 'Assets/ReduxBetterAA/Copied/swinfo.json').read_bytes(),
            'Built manifest differs from the current source')
    payload.update({target: (root / source).read_bytes() for target, source in DOCS.items()})
    manifest = {
        'schemaVersion': 2, 'version': version, 'sourceCommit': commit,
        'sdkSha256': digest(Path(sdk).read_bytes()), 'includesNativeLibraries': False,
        'reduxVersions': ['0.2.8.5', '0.2.9.0'], 'removedBuildMetadata': removed,
        'files': [{'path': name, 'bytes': len(data), 'sha256': digest(data)} for name, data in sorted(payload.items())],
    }
    payload['package-manifest.json'] = encode_json(manifest)
    mod = Path(output) / f'ReduxBetterAA-{version}.zip'
    write_zip(mod, {PREFIX + name: data for name, data in payload.items()})
    verify(mod, commit, version)
    return mod


def verify(path, commit, version):
    return verify_files(read_zip(path), commit, version)


def verify_files(files, commit, version):
    require(all(name.startswith(PREFIX) for name in files), 'All files must stay under mods/ReduxBetterAA')
    payload = {name[len(PREFIX):]: data for name, data in files.items()}
    manifest = json.loads(payload.pop('package-manifest.json'))
    require(manifest['schemaVersion'] == 2 and manifest['sourceCommit'] == commit
            and manifest['version'] == version and manifest['includesNativeLibraries'] is False,
            'Package provenance mismatch')
    listed = manifest['files']
    require(len(listed) == len(payload) and {entry['path'] for entry in listed} == set(payload), 'Manifest file list mismatch')
    for entry in listed:
        data = payload[entry['path']]
        require(len(data) == entry['bytes'] and digest(data) == entry['sha256'], f"Content hash mismatch: {entry['path']}")
    for name in DOCS:
        require(name in payload and payload[name], f'Missing documentation/license: {name}')
    validate_payload({name: data for name, data in payload.items() if name not in DOCS}, version)
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    create = sub.add_parser('build')
    create.add_argument('--input', type=Path, required=True)
    create.add_argument('--output', type=Path, required=True)
    create.add_argument('--commit', required=True)
    check = sub.add_parser('verify')
    check.add_argument('zip', type=Path)
    check.add_argument('--commit', required=True)
    check.add_argument('--version', required=True)
    args = parser.parse_args()
    if args.command == 'build':
        path = build(args.input, args.output, args.commit)
        print(f'{digest(path.read_bytes())}  {path}')
    else:
        verify(args.zip, args.commit, args.version)
        print('Release archive verified.')


if __name__ == '__main__':
    main()
