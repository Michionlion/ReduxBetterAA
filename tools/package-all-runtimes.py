"""Build one complete native runtime download per supported engine version."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'tools' / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


runtimes = load('runtime_components', 'package-runtimes.py')
fg = load('fg_components', 'package-frame-generation.py')
MANIFEST = 'BetterAA-Runtimes-manifest.json'


def merge(payload, extra):
    if payload.keys() & extra.keys():
        raise ValueError('Runtime components contain overlapping paths')
    payload.update(extra)


def assemble(nvidia, fsr, frame_generation, version):
    packages = {}
    for unity, target in runtimes.TARGETS.items():
        name = f"BetterAA-Runtimes-Redux-{target['redux']}.zip"
        payload = dict(nvidia[name])
        merge(payload, fsr)
        includes_fg = unity == fg.PINS['unityVersion'] and target['redux'] == fg.PINS['reduxVersion']
        if includes_fg:
            merge(payload, frame_generation)
        manifest = {'schemaVersion': 1, 'modVersion': version, 'unityVersion': unity,
                    'reduxVersion': target['redux'], 'frameGenerationIncluded': includes_fg,
                    'files': [{'path': path, 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
                              for path, data in sorted(payload.items())]}
        payload[MANIFEST] = (json.dumps(manifest, indent=2) + '\n').encode()
        packages[name] = payload
    return packages


def collect(editors, fsr_zip, fg_zip, version):
    runtimes.validate_fsr_archive(fsr_zip, version)
    manifest = fg.verify(fg_zip, version)
    if set(manifest['vendors']) != {'amd', 'nvidia'}:
        raise ValueError('Complete runtime bundles require both FG vendors')
    with zipfile.ZipFile(fsr_zip) as archive:
        fsr_payload = {n: archive.read(n) for n in archive.namelist()}
    with zipfile.ZipFile(fg_zip) as archive:
        fg_payload = {n: archive.read(n) for n in archive.namelist()}
    return assemble(runtimes.collect(editors), fsr_payload, fg_payload, version)


def package(packages, output):
    output = Path(output)
    for name in packages:
        if (output / name).exists():
            raise FileExistsError(output / name)
    output.mkdir(parents=True, exist_ok=True)
    for name, payload in packages.items():
        path = output / name
        with zipfile.ZipFile(path, 'x', compression=zipfile.ZIP_DEFLATED) as archive:
            for member, data in sorted(payload.items()):
                info = zipfile.ZipInfo(member, (2026, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                archive.writestr(info, data)
        with zipfile.ZipFile(path) as archive:
            if len(archive.namelist()) != len(payload) or any(archive.read(n) != b for n, b in payload.items()):
                raise ValueError(f'Combined runtime verification failed: {path}')
        print(path)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--editors', nargs='+', required=True)
    parser.add_argument('--fsr-runtime', type=Path, required=True)
    parser.add_argument('--frame-generation', type=Path, required=True)
    parser.add_argument('--mod-version', required=True)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    packages = collect(args.editors, args.fsr_runtime, args.frame_generation, args.mod_version)
    if args.output:
        package(packages, args.output)
