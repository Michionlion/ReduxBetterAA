"""Package the optional player runtimes from locally installed, pinned editors."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[1]
TARGETS = json.loads((ROOT / 'tools/runtime-targets.json').read_text(encoding='utf-8'))
PLAYER = Path('Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_player_nondevelopment_mono')


def collect(editors):
    packages = {}
    for unity, target in TARGETS.items():
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
        payload['BetterAA-Runtime-Notices.txt'] = (header + (ROOT / 'licenses/Native-Runtimes.txt').read_text(encoding='utf-8')).encode('utf-8')
        packages[f"BetterAA-Runtimes-Redux-{target['redux']}.zip"] = payload
    return packages


def package(packages, output):
    output = Path(output)
    # Refuse collisions before writing anything; never replace another build.
    for name in packages:
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
    parser.add_argument('--editors', nargs='+', required=True, help='Matching Unity Editor directories')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    packages = collect(args.editors)
    if args.output:
        package(packages, args.output)
