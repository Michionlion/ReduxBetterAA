"""Build/verify an explicit optional FG companion from local pinned inputs; never download or install."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import stat
import struct
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PINS = json.loads((ROOT / 'tools/frame-generation-targets.json').read_text(encoding='utf-8'))
PREFIX = PINS['nativePrefix']
MANIFEST = PREFIX + 'frame-generation-manifest.json'
COORDINATOR = PINS['coordinatorPath']
EXPORTS = {
    'coordinator': {'UnityPluginLoad', 'UnityPluginUnload', 'UnityRenderingExtQuery',
                    'RbaFgConfigureV2', 'RbaFgGetStatusV2', 'RbaFgGetRenderEventFunc'},
    'nvidia': {'RbaSlFgCreate', 'RbaSlFgGetInterfaces', 'RbaSlFgSubmit', 'RbaSlFgPoll', 'RbaSlFgDestroy'},
    'amd': {'RbaAmdProviderCreate', 'RbaAmdProviderGetInterfaces', 'RbaAmdProviderSubmit',
            'RbaAmdProviderPoll', 'RbaAmdProviderDestroy'},
}


def require(value, message):
    if not value:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def encoded(data):
    return (json.dumps(data, indent=2, sort_keys=True) + '\n').encode('utf-8')


def outside(path, root=ROOT):
    value = Path(path).resolve()
    require(value != root.resolve() and root.resolve() not in value.parents,
            f'External dependencies/output must stay outside source: {value}')
    return value


def checked_file(directory, pin):
    path = Path(directory) / pin['source']
    data = path.read_bytes()
    require(sha(data) == pin['sha256'], f"Pinned file mismatch: {pin['source']}")
    return data


def check_git(directory, commit, paths):
    actual = subprocess.check_output(['git', '-C', str(directory), 'rev-parse', 'HEAD'], text=True).strip()
    require(actual == commit, f'Wrong SDK checkout: {directory}')
    subprocess.run(['git', '-C', str(directory), 'diff', '--exit-code', 'HEAD', '--', *paths], check=True,
                   stdout=subprocess.DEVNULL)


def pe_exports(data):
    """Inspect PE metadata only; do not load or execute package inputs."""
    require(len(data) >= 256 and data[:2] == b'MZ', 'Expected a native Windows PE DLL')
    pe = struct.unpack_from('<I', data, 60)[0]
    require(pe + 264 <= len(data) and data[pe:pe + 4] == b'PE\0\0', 'Invalid PE header')
    machine, count = struct.unpack_from('<HH', data, pe + 4)
    optional_size, characteristics = struct.unpack_from('<HH', data, pe + 20)
    optional = pe + 24
    require(machine == 0x8664 and characteristics & 0x2000 and optional_size >= 240 and
            struct.unpack_from('<H', data, optional)[0] == 0x20b, 'Expected a Windows x64 DLL')
    sections = optional + optional_size
    require(0 < count <= 96 and sections + count * 40 <= len(data), 'Invalid PE section table')

    def offset(rva, size=1):
        for index in range(count):
            virtual_size, address, raw_size, raw = struct.unpack_from('<IIII', data, sections + index * 40 + 8)
            if address <= rva and rva + size <= address + max(virtual_size, raw_size):
                location = raw + rva - address
                require(rva + size <= address + raw_size and location + size <= len(data), 'PE RVA has no stored bytes')
                return location
        raise ValueError('PE RVA outside its sections')

    rva, size = struct.unpack_from('<II', data, optional + 112)
    require(rva and size >= 40, 'DLL has no export directory')
    exports = offset(rva, 40)
    names_count = struct.unpack_from('<I', data, exports + 24)[0]
    names_rva = struct.unpack_from('<I', data, exports + 32)[0]
    require(0 < names_count <= 100000, 'Invalid DLL export count')
    names = offset(names_rva, names_count * 4)
    result = set()
    for index in range(names_count):
        name = offset(struct.unpack_from('<I', data, names + index * 4)[0])
        end = data.find(b'\0', name, min(name + 512, len(data)))
        require(end >= name, 'Unterminated DLL export')
        result.add(data[name:end].decode('ascii'))
    return result


def native_file(path, role):
    data = Path(path).read_bytes()
    require(EXPORTS[role] <= pe_exports(data), f'Missing {role} ABI exports')
    return data


def validate_source_pins(root=ROOT):
    amd = json.loads((root / 'Native/FrameGeneration/runtime-manifest.json').read_text(encoding='utf-8'))
    require(amd['sdk']['commit'] == PINS['vendors']['amd']['sdkCommit'], 'AMD packaging/source SDK pin drift')
    require({e['filename']: e['sha256'] for e in amd['files']} ==
            {e['target']: e['sha256'] for e in PINS['vendors']['amd']['runtimeFiles']}, 'AMD packaging/loader hash pin drift')
    source = (root / 'Native/Streamline/StreamlineRuntime.h').read_text(encoding='utf-8')
    runtime_hashes = dict(re.findall(r'\{L"([^"]+\.dll)", "([0-9a-f]{64})"\}', source))
    require(runtime_hashes == {e['target']: e['sha256'] for e in PINS['vendors']['nvidia']['runtimeFiles']},
            'NVIDIA packaging/loader hash pin drift')
    presentation = (root / 'Native/Presentation/CMakeLists.txt').read_text(encoding='utf-8')
    require(all(e['sha256'] in presentation for e in PINS['unityFiles'] if e['source'].endswith('.h')),
            'Coordinator/package Unity header pin drift')


def expected_paths(vendors):
    require(vendors and set(vendors) <= set(PINS['vendors']) and len(vendors) == len(set(vendors)), 'Select unique supported vendors')
    paths = {COORDINATOR, PREFIX + 'LICENSE-BetterAA.txt', PREFIX + 'INSTALL-FRAME-GENERATION.txt',
             PREFIX + 'licenses/unity/PluginAPI-LICENSE.md', PREFIX + 'licenses/unity/Unity-Companion-License.txt'}
    for name in vendors:
        pin = PINS['vendors'][name]
        paths.add(PREFIX + pin['provider'])
        paths.update(PREFIX + pin['runtimeSubdirectory'] + '/' + f['target'] for f in pin['runtimeFiles'])
        paths.update(PREFIX + 'licenses/' + name + '/' + f['target'] for f in pin['licenseFiles'])
    return paths


def package(output, coordinator, unity_api, vendors, provider_paths, runtime_roots, sdk_roots, root=ROOT):
    validate_source_pins(root)
    output = outside(output, root)
    require(not output.exists(), f'Output already exists: {output}')
    expected_paths(vendors)
    unity_api = outside(unity_api, root)
    for pin in PINS['unityFiles']:
        checked_file(unity_api, pin)
    payload = {COORDINATOR: native_file(outside(coordinator, root), 'coordinator'),
               PREFIX + 'LICENSE-BetterAA.txt': (root / 'LICENSE').read_bytes(),
               PREFIX + 'licenses/unity/PluginAPI-LICENSE.md': (unity_api / 'LICENSE.md').read_bytes(),
               PREFIX + 'licenses/unity/Unity-Companion-License.txt': (root / 'tools/licenses/Unity-Companion-License.txt').read_bytes()}
    for name in vendors:
        pin = PINS['vendors'][name]
        sdk = outside(sdk_roots[name], root)
        paths = ['include', 'license.txt', '3rd-party-licenses.md'] if name == 'nvidia' else [
            'Kits/FidelityFX/api/include', 'Kits/FidelityFX/framegeneration/include', 'docs/license.md', '3rdpartynotice.md']
        check_git(sdk, pin['sdkCommit'], paths)
        runtime = outside(runtime_roots[name], root)
        payload[PREFIX + pin['provider']] = native_file(outside(provider_paths[name], root), name)
        for item in pin['runtimeFiles']:
            payload[PREFIX + pin['runtimeSubdirectory'] + '/' + item['target']] = checked_file(runtime, item)
        for item in pin['licenseFiles']:
            payload[PREFIX + 'licenses/' + name + '/' + item['target']] = checked_file(runtime, item)
    version = json.loads((root / 'Assets/ReduxBetterAA/Copied/swinfo.json').read_text(encoding='utf-8'))['version']
    require(re.fullmatch(r'(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)', version), 'Invalid mod version')
    instructions = (f'Redux Better AA {version}: optional frame generation companion\n'
                    f'Redux {PINS["reduxVersion"]}; Unity {PINS["unityVersion"]}; Windows x64 D3D11 player.\n'
                    f'Included providers: {", ".join(sorted(vendors))}. Install the matching main mod separately.\n\n'
                    'Close the player, preserve different existing files, and extract at the game root beside KSP2_x64.exe.\n'
                    'Keep the archive paths intact. The coordinator belongs in KSP2_x64_Data/Plugins/x86_64;\n'
                    'the host asks Unity to load it through the native plugin mechanism and verifies its path.\n'
                    'Provider/runtime files belong under mods/ReduxBetterAA/native/frame-generation.\n'
                    'No game/Unity runtime DLL is replaced. No setting is enabled by this archive alone.\n'
                    'Select Frame generation only when its actual backend capability is available.\n'
                    'To uninstall, close the player and remove exactly the files listed in this archive manifest.\n\n'
                    'Vendor binaries are unmodified and governed by their included original licenses, not Better AA MIT.\n'
                    + ('This software contains source code provided by NVIDIA Corporation.\n' if 'nvidia' in vendors else '') +
                    'Unity Native Plugin API copyright © 2015 Unity Technologies ApS.\n'
                    'Native/package validation does not certify player image quality, latency, or untested AMD FG4 hardware.\n')
    payload[PREFIX + 'INSTALL-FRAME-GENERATION.txt'] = instructions.encode('utf-8')
    require(set(payload) == expected_paths(vendors), 'Internal companion payload mismatch')
    manifest = {'schemaVersion': 1, 'kind': 'optional-frame-generation', 'modVersion': version,
                'unityVersion': PINS['unityVersion'], 'reduxVersion': PINS['reduxVersion'],
                'coordinatorAbi': PINS['coordinatorAbi'], 'providerAbi': PINS['providerAbi'],
                'vendors': sorted(vendors), 'sourcePinsSha256': sha(encoded(PINS)),
                'files': [{'path': name, 'bytes': len(data), 'sha256': sha(data)} for name, data in sorted(payload.items())]}
    payload[MANIFEST] = encoded(manifest)
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, 'x', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(payload.items()):
            entry = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, data)
    verify(output, version)
    return output


def verify(path, version):
    payload = {}
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        require(len(entries) <= 64 and sum(e.file_size for e in entries) < 512 * 1024 * 1024, 'FG archive exceeds payload limits')
        seen = set()
        for entry in entries:
            name = entry.orig_filename
            require(not entry.is_dir() and not stat.S_ISLNK(entry.external_attr >> 16), 'FG links/directories are forbidden')
            require(name.casefold() not in seen and not name.startswith('/') and not re.search(r'[\\:\x00-\x1f]', name)
                    and all(p not in ('', '.', '..') and not p.endswith(('.', ' ')) for p in name.split('/')), 'Unsafe/duplicate FG archive path')
            seen.add(name.casefold())
            payload[name] = archive.read(entry)
    require(MANIFEST in payload, 'FG manifest missing')
    manifest = json.loads(payload.pop(MANIFEST))
    vendors = manifest.get('vendors', [])
    require(set(payload) == expected_paths(vendors), 'Unexpected/missing optional FG files')
    require(manifest.get('schemaVersion') == 1 and manifest.get('kind') == 'optional-frame-generation' and
            manifest.get('modVersion') == version and manifest.get('unityVersion') == PINS['unityVersion'] and
            manifest.get('reduxVersion') == PINS['reduxVersion'] and manifest.get('coordinatorAbi') == PINS['coordinatorAbi'] and
            manifest.get('providerAbi') == PINS['providerAbi'] and manifest.get('sourcePinsSha256') == sha(encoded(PINS)),
            'FG package version/ABI/source pin mismatch')
    listed = manifest.get('files', [])
    require(len(listed) == len(payload) and {e['path'] for e in listed} == set(payload), 'FG hash manifest file list mismatch')
    for entry in listed:
        data = payload[entry['path']]
        require(entry['bytes'] == len(data) and entry['sha256'] == sha(data), f"FG content hash mismatch: {entry['path']}")
    require(EXPORTS['coordinator'] <= pe_exports(payload[COORDINATOR]), 'Coordinator lacks required ABI2 exports')
    unity_pin = next(p for p in PINS['unityFiles'] if p['source'] == 'LICENSE.md')
    require(sha(payload[PREFIX + 'licenses/unity/PluginAPI-LICENSE.md']) == unity_pin['sha256'], 'Unity PluginAPI notice pin mismatch')
    require(sha(payload[PREFIX + 'LICENSE-BetterAA.txt']) == PINS['betterAaLicenseSha256'] and
            sha(payload[PREFIX + 'licenses/unity/Unity-Companion-License.txt']) == PINS['unityCompanionLicenseSha256'],
            'Required Better AA/Unity license pin mismatch')
    for name in vendors:
        pin = PINS['vendors'][name]
        require(EXPORTS[name] <= pe_exports(payload[PREFIX + pin['provider']]), f'Missing {name} provider ABI')
        for item in pin['runtimeFiles']:
            require(sha(payload[PREFIX + pin['runtimeSubdirectory'] + '/' + item['target']]) == item['sha256'], 'FG vendor runtime pin mismatch')
        for item in pin['licenseFiles']:
            require(sha(payload[PREFIX + 'licenses/' + name + '/' + item['target']]) == item['sha256'], 'FG vendor license pin mismatch')
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    create = commands.add_parser('build')
    create.add_argument('--output', required=True, type=Path)
    create.add_argument('--coordinator', required=True, type=Path)
    create.add_argument('--unity-plugin-api', required=True, type=Path)
    create.add_argument('--vendors', nargs='+', choices=['nvidia', 'amd'], required=True)
    for vendor in PINS['vendors']:
        for part in ('provider', 'sdk', 'runtime'):
            create.add_argument(f'--{vendor}-{part}', type=Path)
    check = commands.add_parser('verify')
    check.add_argument('zip', type=Path)
    check.add_argument('--mod-version', required=True)
    args = parser.parse_args()
    if args.command == 'verify':
        print(json.dumps(verify(args.zip, args.mod_version), indent=2))
        return
    paths = {part: {vendor: getattr(args, f'{vendor}_{part}') for vendor in args.vendors} for part in ('provider', 'sdk', 'runtime')}
    require(all(all(group.values()) for group in paths.values()), 'Each selected vendor needs explicit provider, SDK and runtime paths')
    result = package(args.output, args.coordinator, args.unity_plugin_api, args.vendors, paths['provider'], paths['runtime'], paths['sdk'])
    print(f'{sha(result.read_bytes())}  {result}')


if __name__ == '__main__':
    main()
