"""Validate release coverage against production diagnostics and captured artifacts."""
import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path
import struct
import zipfile
import zlib

MODES = [('Off', 'Off'), ('FxaaLow', 'FXAA Low'), ('FxaaHigh', 'FXAA High'),
         ('Smaa', 'SMAA'), ('CustomTaa', 'Custom TAA'), ('NvidiaDlaa', 'NVIDIA DLAA'),
         ('AmdFsr2', 'FSR2 Native AA'), ('Supersampling', 'Supersampling')]
CAMERAS = {'MainMenu': ('Camera.Scaled', 'Skybox'),
           'FlightView': ('FlightCameraPhysics_Main', 'FlightCameraScaled_Main'),
           'Map3DView': ('MapCamera', '')}
TEMPORAL = {'Custom TAA', 'NVIDIA DLAA', 'FSR2 Native AA'}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'),
                      object_hook=lambda value: {k: v for k, v in value.items() if k != '$type'})


def coverage(capabilities, phase):
    rows = []
    if phase == 'core':
        rows.append(dict(scene='MainMenu', requested='CustomTaa', expected='Custom TAA',
                         mapEnabled=True, label='new-install'))
    for scene in CAMERAS:
        for requested, selected in MODES:
            if ((requested == 'NvidiaDlaa' and not capabilities['dlaa']) or
                    (requested == 'AmdFsr2' and not capabilities['fsr2']) or
                    (requested == 'Supersampling' and scene != 'FlightView')):
                selected = 'Off'
            rows.append(dict(scene=scene, requested=requested, expected=selected,
                             mapEnabled=True, label=requested))
        rows.append(dict(scene=scene, requested='Off', expected='Off', mapEnabled=True, label='cleanup'))
        if scene == 'FlightView':
            rows.extend(dict(scene=scene, requested=mode, expected=selected, mapEnabled=True, label=label)
                        for mode, selected, label in [('4', 'Custom TAA', 'legacy-mode'), ('999', 'Off', 'unknown-mode')])
    for scene, mode, selected, enabled, label in [
        ('Map3DView', 'CustomTaa', 'Off', False, 'map-disabled'),
        ('Map3DView', 'CustomTaa', 'Custom TAA', True, 'map-restored'),
        ('FlightView', 'CustomTaa', 'Custom TAA', True, 'reload'),
        ('FlightView', 'CustomTaa', 'Custom TAA', True, 'unpaused'),
        ('FlightView', 'Off', 'Off', True, 'final-cleanup')]:
        rows.append(dict(scene=scene, requested=mode, expected=selected, mapEnabled=enabled, label=label))
    return rows


def png_size(data):
    require(data[:8] == b'\x89PNG\r\n\x1a\n', 'Missing PNG signature')
    offset, compressed, size = 8, bytearray(), None
    while offset + 12 <= len(data):
        length = struct.unpack_from('>I', data, offset)[0]
        chunk = data[offset + 4:offset + 8]
        payload = data[offset + 8:offset + 8 + length]
        require(offset + 12 + length <= len(data), 'Truncated PNG chunk')
        require(zlib.crc32(chunk + payload) == struct.unpack_from('>I', data, offset + 8 + length)[0], 'PNG checksum mismatch')
        if chunk == b'IHDR':
            size = struct.unpack_from('>II', payload)
        if chunk == b'IDAT':
            compressed.extend(payload)
        offset += 12 + length
        if chunk == b'IEND':
            require(size and min(size) > 0 and len(zlib.decompress(compressed)) > 0, 'Empty PNG image')
            require(offset == len(data), 'Unexpected bytes after PNG')
            return size
    raise ValueError('Missing PNG end chunk')


def snapshot(data, row, assembly_hash):
    temporal, graph = data['temporal'], data['cameraGraph']
    scene, selected = row['scene'], row['expected']
    requested = {'4': 'CustomTaa', '999': 'Off'}.get(row['requested'], row['requested'])
    camera_name, shared = CAMERAS[scene]
    scale = temporal['supersamplingPercent'] if selected == 'Supersampling' else 100
    expected = dict(requestedBackend=requested, selectedBackend=selected, active=selected != 'Off',
                    resolveCamera=camera_name, sharedJitterCamera=shared, projectionJitterSupported=True,
                    jitterTransparentRendering=scene != 'FlightView', appliedRenderScalePercent=scale,
                    mapViewAaEnabled=row['mapEnabled'],
                    mapViewAaOverrideActive=scene == 'Map3DView' and not row['mapEnabled'] and requested != 'Off')
    require(data['captureReason'] == 'Manual', 'Not a production manual capture')
    require(data['runtime']['modAssemblySha256'].lower() == assembly_hash, 'Different candidate assembly')
    require(graph['gameState'] == scene, 'Wrong game state')
    for key, wanted in expected.items():
        require(temporal[key] == wanted, f'{key}: expected {wanted!r}, found {temporal[key]!r}')
    fallback = requested != 'Off' and selected == 'Off' and row['mapEnabled']
    require(bool(temporal['fallbackReason']) == fallback, 'Missing or unexpected fallback reason')
    cameras = [c for c in graph['cameras'] if c['name'] == camera_name and c['enabled'] and c['activeInHierarchy']]
    require(len(cameras) == 1, 'Expected exactly one active resolve camera')
    camera = cameras[0]
    require(camera['components'].count('ReduxBetterAA.Rendering.TemporalRenderHook') == int(selected in TEMPORAL), 'Temporal hook ownership mismatch')
    target = camera['targetTexture']
    size = (target['width'], target['height']) if target['present'] else (camera['pixelWidth'], camera['pixelHeight'])
    require(size == tuple((graph[key] * scale + 99) // 100 for key in ('screenWidth', 'screenHeight')), 'Incorrect scene resolution')
    require(not target['present'] or target['created'], 'Resolve target lost GPU storage')
    ppv2 = 'FastApproximateAntialiasing' if selected.startswith('FXAA') else 'SubpixelMorphologicalAntialiasing' if selected == 'SMAA' else 'None'
    require(camera['postProcessAntialiasing'] == ppv2, 'PPv2 ownership mismatch')
    overlays = [b for b in camera['commandBuffers'] if 'Better AA map icons (after AA)' in b['names']]
    require(len(overlays) == int(scene == 'Map3DView' and selected != 'Off'), 'Map overlay missing or duplicated')
    if selected in TEMPORAL:
        require(all(flag in camera['depthTextureMode'] for flag in ('Depth', 'MotionVectors')), 'Temporal depth/motion flags missing')
    if selected == 'Off':
        for key in ('customEstimatedMemoryBytes', 'dlaaEstimatedMemoryBytes', 'fsr2EstimatedMemoryBytes',
                    'motionVectorSanitizerEstimatedMemoryBytes', 'depthDisocclusionMaskEstimatedMemoryBytes'):
            require(temporal[key] == 0, f'Off retained {key}')
        require(not temporal['vegetationMotionRepairEnabled'], 'Off retained foliage override')
    if selected in ('NVIDIA DLAA', 'FSR2 Native AA'):
        vendor = temporal['dlaa' if selected == 'NVIDIA DLAA' else 'fsr2']
        require(vendor['contextCreated'] and vendor['nativeResolution'] and vendor['outputRandomWrite'] and not vendor['lastFailure'], 'Vendor context is not healthy')
        require((vendor['inputWidth'], vendor['inputHeight']) == size == (vendor['outputWidth'], vendor['outputHeight']), 'Vendor dimensions mismatch')
    return graph['screenWidth'], graph['screenHeight']


def issue_archive(path, assembly_hash):
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)), 'Duplicate Issue ZIP entries')
        manifest = json.loads(archive.read('manifest.json'))
        require(manifest['status'] == 'complete' and not manifest['errors'], 'Issue ZIP is partial')
        require(manifest['inputStage'] == 'before-temporal-resolve', 'Wrong Issue ZIP capture stage')
        require(manifest['inputFrame'] >= 0 and manifest['inputFrame'] == manifest['outputFrame'] == manifest['screenshotFrame'], 'Issue ZIP frame mismatch')
        files = manifest['files']
        require(len(files) == len({f['path'] for f in files}) and set(names) == {f['path'] for f in files} | {'manifest.json'}, 'Issue ZIP manifest differs from payload')
        for entry in files:
            data = archive.read(entry['path'])
            require(len(data) == entry['bytes'] and hashlib.sha256(data).hexdigest() == entry['sha256'], f"Issue ZIP changed: {entry['path']}")
        capabilities = json.loads(archive.read('capabilities.json'))
        require(capabilities['runtime']['modAssemblySha256'].lower() == assembly_hash, 'Issue ZIP belongs to another build')
        require(capabilities['temporal']['selectedBackend'] == 'Custom TAA' and capabilities['cameraGraph']['gameState'] == 'FlightView', 'Issue ZIP backend/scene mismatch')
        for name in ('scene-input', 'scene-output', 'depth-device', 'motion-raw'):
            buffers = [b for b in manifest['buffers'] if b['name'] == name and b['status'] == 'captured']
            require(len(buffers) == 1, f'Missing Issue ZIP buffer: {name}')
            buffer = buffers[0]
            require(bool(archive.read(buffer['rawFile'])), f'Empty Issue ZIP buffer: {name}')
            require(png_size(archive.read(buffer['previewFile'])) == (buffer['width'], buffer['height']), f'Wrong Issue ZIP buffer dimensions: {name}')
        png_size(archive.read('presented.png'))


def validate(report_path, diagnostics, assembly, phase):
    report = read_json(report_path)
    assembly_hash = hashlib.sha256(assembly.read_bytes()).hexdigest()
    require(report['status'] == 'passed' and not report['errors'] and not report['process']['crashed'], 'Harness run did not pass')
    require(report['assertions'] and all(a['status'] == 'passed' for a in report['assertions']), 'Harness assertions failed or absent')
    require(report['values']['native'] == (phase == 'native'), 'Wrong native-runtime test phase')
    capabilities = report['values']['capabilities']
    require(all(type(capabilities[k]) is bool for k in ('dlaa', 'fsr2')), 'Invalid vendor capabilities')
    require((capabilities['fsr2'] if phase == 'native' else not capabilities['dlaa'] and not capabilities['fsr2']), 'Unexpected vendor availability')
    rows = coverage(capabilities, phase)
    require(report['values']['captures'] == {str(i): row for i, row in enumerate(rows, 1)}, 'Release suite coverage is incomplete or changed')
    files = sorted(p for p in diagnostics.glob('phase1-*.json') if p.name != 'phase1-latest.json')
    files = [(p, read_json(p)) for p in files]
    files = [(p, d) for p, d in files if d['captureReason'] == 'Manual']
    require(len(files) == len(rows), f'Expected {len(rows)} reports; found {len(files)}')
    start, end = (datetime.fromisoformat(report[k].replace('Z', '+00:00')) for k in ('startedUtc', 'endedUtc'))
    errors, captures = [], []
    for (path, data), row in zip(files, rows):
        try:
            captured = datetime.fromisoformat(data['capturedUtc'].replace('Z', '+00:00'))
            require(start <= captured <= end, 'Capture belongs to another run')
            size = snapshot(data, row, assembly_hash)
            images = list((diagnostics / 'screenshots').glob('phase1-*-' + path.stem.rsplit('-', 1)[1] + '-*.png'))
            require(len(images) == 1 and png_size(images[0].read_bytes()) == size, 'Missing or wrong-sized production screenshot')
        except (KeyError, ValueError, OSError, struct.error, zlib.error) as error:
            errors.append(f'{path.name}: {error}')
        captures.append(row | {'report': path.name})
    issue = Path(report['values']['issueZip'])
    if not issue.is_absolute():
        issue = report_path.parent / issue
    issue_archive(issue, assembly_hash)
    require(len(report['screenshots']) == 1, 'Missing final UI screenshot')
    png_size((report_path.parent / report['screenshots'][0]).read_bytes())
    return dict(passed=not errors, phase=phase, modSha256=assembly_hash, environment=report['environment'],
                capabilities=capabilities, reports=len(files), errors=errors, captures=captures)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('report', 'diagnostics', 'assembly', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--phase', choices=('core', 'native'), required=True)
    args = parser.parse_args()
    try:
        result = validate(args.report, args.diagnostics, args.assembly, args.phase)
    except (KeyError, ValueError, OSError, struct.error, zipfile.BadZipFile, zlib.error) as error:
        result = dict(passed=False, phase=args.phase, errors=[str(error)])
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: value for key, value in result.items() if key != 'captures'}, indent=2))
    raise SystemExit(0 if result['passed'] else 1)
