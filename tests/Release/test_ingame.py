import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest
import zipfile
import zlib

spec = importlib.util.spec_from_file_location('ingame', Path(__file__).with_name('validate-ingame.py'))
ingame = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ingame)
HASH = 'a' * 64


def png():
    def chunk(name, data):
        return struct.pack('>I', len(data)) + name + data + struct.pack('>I', zlib.crc32(name + data))
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 1, 1, 8, 2, 0, 0, 0)) +
            chunk(b'IDAT', zlib.compress(b'\0\x20\x40\x80')) + chunk(b'IEND', b''))


class InGameTests(unittest.TestCase):
    def test_validate_off_ownership_and_reject_leaked_state_or_another_build(self):
        row = ingame.coverage({'dlaa': False, 'fsr2': False})[0]
        temporal = dict(requestedBackend='Off', selectedBackend='Off', active=False,
                        resolveCamera='Camera.Scaled', sharedJitterCamera='Skybox',
                        projectionJitterSupported=True, jitterTransparentRendering=True,
                        appliedRenderScalePercent=100, mapViewAaEnabled=True, mapViewAaOverrideActive=False,
                        fallbackReason='', vegetationMotionRepairEnabled=False)
        for key in ('customEstimatedMemoryBytes', 'dlaaEstimatedMemoryBytes', 'fsr2EstimatedMemoryBytes',
                    'motionVectorSanitizerEstimatedMemoryBytes', 'depthDisocclusionMaskEstimatedMemoryBytes'):
            temporal[key] = 0
        camera = dict(name='Camera.Scaled', enabled=True, activeInHierarchy=True, components=[],
                      targetTexture={'present': False}, pixelWidth=1280, pixelHeight=720,
                      postProcessAntialiasing='None', commandBuffers=[])
        data = dict(captureReason='Manual', runtime={'modAssemblySha256': HASH}, temporal=temporal,
                    cameraGraph=dict(gameState='MainMenu', screenWidth=1280, screenHeight=720, cameras=[camera]))
        self.assertEqual(ingame.snapshot(data, row, HASH), (1280, 720))
        for target, key, value, error in [
            (temporal, 'customEstimatedMemoryBytes', 1024, 'retained'),
            (camera, 'postProcessAntialiasing', 'FastApproximateAntialiasing', 'ownership'),
            (camera, 'components', ['ReduxBetterAA.Rendering.TemporalRenderHook'], 'ownership'),
            (data['runtime'], 'modAssemblySha256', 'b' * 64, 'candidate')]:
            old = target[key]
            target[key] = value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, error):
                ingame.snapshot(data, row, HASH)
            target[key] = old

    def test_native_matrix_keeps_unavailable_modes_and_lifecycle_checks(self):
        core = ingame.coverage({'dlaa': False, 'fsr2': False})
        native = ingame.coverage({'dlaa': True, 'fsr2': True})
        self.assertEqual(len(core), 34)
        self.assertEqual([r['label'] for r in native[-5:]],
                         ['map-disabled', 'map-restored', 'reload', 'unpaused', 'final-cleanup'])
        self.assertTrue(all(r['expected'] == 'Off' for r in core if r['requested'] in ('NvidiaDlaa', 'AmdFsr2')))
        self.assertEqual(sum(r['expected'] == 'NVIDIA DLAA' for r in native), 3)

    def test_capture_integrity_and_issue_zip_hashes(self):
        image = png()
        self.assertEqual(ingame.png_size(image), (1, 1))
        for broken in (image[:-8], image[:40] + bytes([image[40] ^ 1]) + image[41:]):
            with self.assertRaises(ValueError):
                ingame.png_size(broken)
        payload = {'presented.png': image, 'capabilities.json': json.dumps(dict(
            runtime={'modAssemblySha256': HASH}, temporal={'selectedBackend': 'Custom TAA'},
            cameraGraph={'gameState': 'FlightView'})).encode()}
        buffers = []
        for name in ('scene-input', 'scene-output', 'depth-device', 'motion-raw'):
            payload[name + '.exr'] = b'raw samples'
            payload[name + '.png'] = image
            buffers.append(dict(name=name, status='captured', width=1, height=1,
                                rawFile=name + '.exr', previewFile=name + '.png'))
        manifest = dict(status='complete', errors=[], inputStage='before-temporal-resolve',
                        inputFrame=50, outputFrame=50, screenshotFrame=50,
                        buffers=buffers,
                        files=[dict(path=name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest())
                               for name, data in payload.items()])
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'issue.zip'
            def write():
                with zipfile.ZipFile(path, 'w') as archive:
                    for name, data in payload.items():
                        archive.writestr(name, data)
                    archive.writestr('manifest.json', json.dumps(manifest))
            write()
            ingame.issue_archive(path, HASH)
            payload['presented.png'] = b'corrupt'
            write()
            with self.assertRaisesRegex(ValueError, 'changed'):
                ingame.issue_archive(path, HASH)
            payload['presented.png'] = image
            manifest['screenshotFrame'] += 1
            write()
            with self.assertRaisesRegex(ValueError, 'frame mismatch'):
                ingame.issue_archive(path, HASH)
