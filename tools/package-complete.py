"""Combine each engine's validated mod component with its pinned native AA runtimes."""
import argparse
import importlib.util
import json
from pathlib import Path
import re
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'tools' / (name + '.py'))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


mod = module('package-release')
runtimes = module('package-runtimes')
MANIFEST = 'BetterAA-release-manifest.json'
LIMIT = 256 * 1024 * 1024


def bundle_engine(data):
    """Return the Unity version that built a UnityFS bundle; players reject newer bundles."""
    mod.require(data.startswith(b'UnityFS\x00') and len(data) > 12, 'Shader bundle is not a UnityFS archive')
    fields = data[12:12 + 64].split(b'\x00')
    mod.require(len(fields) > 2 and re.fullmatch(rb'\d+\.\d+\.\d+[abfpx]\d+', fields[1]), 'Shader bundle lacks a Unity version')
    return fields[1].decode()


def component_engine(files):
    bundles = [name for name in files if re.fullmatch(mod.PREFIX + mod.BUNDLE, name)]
    mod.require(len(bundles) == 1, 'Expected exactly one Better AA Windows bundle')
    return bundle_engine(files[bundles[0]])


def native_payloads(editors, fsr_archive, version):
    runtimes.validate_fsr_archive(fsr_archive, version)
    fsr = mod.read_zip(fsr_archive, LIMIT)
    packages = runtimes.collect(editors)
    return {unity: packages[f"BetterAA-Runtimes-Redux-{target['redux']}.zip"] | fsr
            for unity, target in runtimes.TARGETS.items()}


def build(mod_archives, editors, fsr_archive, output, commit, version):
    """mod_archives maps each Unity version to the component built with that editor."""
    mod.require(set(mod_archives) == set(runtimes.TARGETS), 'Provide one mod component per supported engine')
    components = {}
    for unity, archive in mod_archives.items():
        mod.verify(archive, commit, version)
        components[unity] = mod.read_zip(archive)
        mod.require(component_engine(components[unity]) == unity, f'{archive} was not built with Unity {unity}')
    natives = native_payloads(editors, fsr_archive, version)
    paths = {unity: Path(output) / f"ReduxBetterAA-{version}-Redux-{runtimes.TARGETS[unity]['redux']}.zip"
             for unity in natives}
    for path in paths.values():
        if path.exists():
            raise FileExistsError(path)
    for unity, native in natives.items():
        component = components[unity]
        mod.require(not ({name.casefold() for name in component} & {name.casefold() for name in native}),
                    'Mod and runtime paths overlap')
        payload = component | native
        manifest = {'schemaVersion': 1, 'version': version, 'sourceCommit': commit,
                    'reduxVersion': runtimes.TARGETS[unity]['redux'], 'unityVersion': unity,
                    'includesNativeLibraries': True,
                    'files': [{'path': name, 'bytes': len(data), 'sha256': mod.digest(data)}
                              for name, data in sorted(payload.items())]}
        mod.write_zip(paths[unity], payload | {MANIFEST: mod.encode_json(manifest)})
        verify(paths[unity], commit, version)
    return list(paths.values())


def verify(path, commit, version):
    payload = mod.read_zip(path, LIMIT)
    manifest = json.loads(payload.pop(MANIFEST))
    mod.require(manifest.get('schemaVersion') == 1 and manifest.get('sourceCommit') == commit
                and manifest.get('version') == version and manifest.get('includesNativeLibraries') is True,
                'Complete package provenance mismatch')
    target = runtimes.TARGETS.get(manifest.get('unityVersion'))
    mod.require(target is not None and manifest.get('reduxVersion') == target['redux'], 'Unknown engine pairing')
    entries = manifest['files']
    mod.require(len(entries) == len(payload) and {e['path'] for e in entries} == set(payload), 'Manifest file list mismatch')
    for entry in entries:
        data = payload[entry['path']]
        mod.require(entry['bytes'] == len(data) and entry['sha256'] == mod.digest(data), 'Complete package hash mismatch')
    for name, expected in target['hashes'].items():
        mod.require(mod.digest(payload[name]) == expected, 'Native library differs from pinned engine')
    notice = payload[runtimes.NOTICE].decode('utf-8')
    expected_notice = (f"Redux {target['redux']} / Unity {manifest['unityVersion']} / Windows x64\n"
                       f"Source: https://unity.com/releases/editor/whats-new/{manifest['unityVersion']}\n\n"
                       + (ROOT / 'licenses/Native-Runtimes.txt').read_text(encoding='utf-8'))
    mod.require(notice == expected_notice, 'Native license notices differ')
    fsr_names = {runtimes.FSR_PREFIX + name for name in runtimes.FSR_FILES}
    with tempfile.TemporaryDirectory() as directory:
        fsr = Path(directory) / 'fsr.zip'
        mod.write_zip(fsr, {name: payload[name] for name in fsr_names})
        runtimes.validate_fsr_archive(fsr, version)
    native_names = set(target['hashes']) | {runtimes.NOTICE} | fsr_names
    component = {name: data for name, data in payload.items() if name not in native_names}
    mod.verify_files(component, commit, version)
    mod.require(component_engine(component) == manifest['unityVersion'], 'Shader bundle was built by a different Unity version')
    return manifest


def mod_argument(value):
    unity, separator, path = value.partition('=')
    if not separator or not unity or not path:
        raise argparse.ArgumentTypeError('Expected UNITY_VERSION=PATH')
    return unity, Path(path)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--mod', type=mod_argument, action='append', metavar='UNITY=PATH',
                        help='Mod component built with that Unity editor; one per supported engine')
    parser.add_argument('--editors', nargs='+')
    parser.add_argument('--fsr-runtime', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--verify', type=Path)
    args = parser.parse_args()
    if args.verify:
        verify(args.verify, args.commit, args.version)
    else:
        if not all((args.mod, args.editors, args.fsr_runtime, args.output)):
            parser.error('Build requires --mod, --editors, --fsr-runtime and --output')
        for result in build(dict(args.mod), args.editors, args.fsr_runtime, args.output, args.commit, args.version):
            print(result)
