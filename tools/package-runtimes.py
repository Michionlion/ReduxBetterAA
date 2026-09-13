"""Package the optional NVIDIA player runtimes from pinned Unity editors."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import stat
import zipfile

ROOT = Path(__file__).resolve().parents[1]
TARGETS = json.loads((ROOT / 'tools/runtime-targets.json').read_text(encoding='utf-8'))
PLAYER = Path('Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_player_nondevelopment_mono')
RUNTIME_DLLS = frozenset({'NVUnityPlugin.dll', 'nvngx_dlss.dll'})
NOTICE = 'BetterAA-Runtime-Notices.txt'
FSR_PREFIX = 'mods/ReduxBetterAA/native/'
FSR_FILES = frozenset({'ReduxBetterAA.FsrBridge.dll', 'amd_fidelityfx_upscaler_dx12.dll',
                       'THIRD-PARTY-NOTICES-FSR.txt', 'fsr-runtime-manifest.json'})


def validate_fsr_archive(path, mod_version):
    """Validate a separately built modern FSR companion before installation."""
    if not mod_version:
        raise ValueError('The candidate mod version is required for FSR validation')
    pinned = json.loads((ROOT / 'Native/runtime-manifest.json').read_text(encoding='utf-8'))
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        names = [entry.orig_filename for entry in entries]
        if len(names) != len(set(names)) or set(names) != {FSR_PREFIX + name for name in FSR_FILES}:
            raise ValueError('Unexpected or missing modern FSR archive files')
        if any(entry.is_dir() or stat.S_ISLNK(entry.external_attr >> 16) for entry in entries):
            raise ValueError('FSR archive links or directory entries are not allowed')
        if sum(entry.file_size for entry in entries) > 128 * 1024 * 1024:
            raise ValueError('FSR archive exceeds its payload limit')
        payload = {entry.orig_filename[len(FSR_PREFIX):]: archive.read(entry) for entry in entries}
    manifest = json.loads(payload['fsr-runtime-manifest.json'])
    if (manifest.get('schemaVersion') != 1 or manifest.get('abiVersion') != pinned['abiVersion'] or
            manifest.get('sdk') != pinned['sdk'] or manifest.get('graphicsApi') != pinned['graphicsApi']):
        raise ValueError('FSR manifest differs from the pinned SDK or bridge ABI')
    if manifest.get('modVersion') != mod_version:
        raise ValueError('FSR archive was built for a different Better AA version')
    files = manifest.get('files', [])
    hashes = {entry['filename']: entry['sha256'] for entry in files}
    if len(files) != len(hashes) or set(hashes) != {'ReduxBetterAA.FsrBridge.dll', 'amd_fidelityfx_upscaler_dx12.dll'}:
        raise ValueError('FSR manifest must describe exactly the two native libraries')
    for name, expected in hashes.items():
        if not re.fullmatch('[0-9a-f]{64}', expected) or hashlib.sha256(payload[name]).hexdigest() != expected:
            raise ValueError(f'FSR archive hash mismatch: {name}')
    for entry in pinned['files']:
        if hashes.get(entry['filename']) != entry['sha256']:
            raise ValueError(f"FSR vendor library differs from its pinned hash: {entry['filename']}")
    notices = payload['THIRD-PARTY-NOTICES-FSR.txt'].decode('utf-8-sig')
    if pinned['sdk']['commit'] not in notices or 'AMD' not in notices or 'license' not in notices.lower():
        raise ValueError('FSR runtime license and SDK notices are missing')
    return {'archiveSha256': hashlib.sha256(Path(path).read_bytes()).hexdigest(),
            'modVersion': mod_version, 'sdkCommit': pinned['sdk']['commit'], 'files': hashes}


def collect(editors):
    packages = {}
    for unity, target in TARGETS.items():
        if set(target['hashes']) != RUNTIME_DLLS:
            raise ValueError(f'Expected exactly the two NVIDIA runtime DLLs for {unity}')
        candidates = [Path(editor) / PLAYER for editor in editors if Path(editor).parent.name == unity]
        if len(candidates) != 1:
            raise ValueError(f'Provide exactly one {unity}/Editor directory')
        payload = {}
        for name, expected in target['hashes'].items():
            data = (candidates[0] / name).read_bytes()
            if hashlib.sha256(data).hexdigest() != expected:
                raise ValueError(f'Unexpected {unity} runtime: {name}')
            payload[name] = data
        header = (f"Redux {target['redux']} / Unity {unity} / Windows x64\n"
                  f"Source: https://unity.com/releases/editor/whats-new/{unity}\n\n")
        payload[NOTICE] = (header + (ROOT / 'licenses/Native-Runtimes.txt').read_text(encoding='utf-8')).encode('utf-8')
        packages[f"BetterAA-Runtimes-Redux-{target['redux']}.zip"] = payload
    return packages


def package(packages, output):
    output = Path(output)
    # Refuse collisions before writing anything; never replace another build.
    for name, payload in packages.items():
        if set(payload) != RUNTIME_DLLS | {NOTICE}:
            raise ValueError(f'Unexpected or missing NVIDIA runtime payload: {name}')
        if (output / name).exists():
            raise FileExistsError(output / name)
    output.mkdir(parents=True, exist_ok=True)
    for name, payload in packages.items():
        archive = output / name
        with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED) as zipped:
            for member, data in sorted(payload.items()):
                info = zipfile.ZipInfo(member, (2026, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                zipped.writestr(info, data)
        with zipfile.ZipFile(archive) as zipped:
            if set(zipped.namelist()) != set(payload) or any(zipped.read(k) != v for k, v in payload.items()):
                raise ValueError(f'Runtime archive verification failed: {archive}')
        print(archive)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--editors', nargs='+', help='Matching Unity Editor directories')
    parser.add_argument('--output', type=Path)
    parser.add_argument('--validate-fsr-runtime', type=Path)
    parser.add_argument('--mod-version')
    args = parser.parse_args()
    if args.validate_fsr_runtime:
        if args.editors or args.output:
            parser.error('FSR validation is separate from NVIDIA runtime packaging')
        print(json.dumps(validate_fsr_archive(args.validate_fsr_runtime, args.mod_version)))
    else:
        if not args.editors or args.mod_version:
            parser.error('Provide --editors for NVIDIA runtime packaging')
        packages = collect(args.editors)
        if args.output:
            package(packages, args.output)
