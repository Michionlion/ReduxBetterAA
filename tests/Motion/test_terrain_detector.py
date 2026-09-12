"""Detector controls: coherent changes must survive spatial averaging; dither must not."""
import importlib.util
from pathlib import Path
import unittest
import numpy as np

spec=importlib.util.spec_from_file_location('detector',Path(__file__).parents[2]/'tools/analyze-terrain-flicker.py')
detector=importlib.util.module_from_spec(spec);spec.loader.exec_module(detector)


class TerrainDetectorTests(unittest.TestCase):
    def test_noise_and_coherent_changes_are_distinguished(self):
        rng=np.random.default_rng(713)
        image=np.full((96,32,64,3),.4,np.float32)
        image+=rng.normal(0,.002,image.shape).astype(np.float32)
        valid=np.ones(95,bool)
        noise=detector.coherent_blocks(image,valid)['16']['mae']
        self.assertEqual(detector.classify_flicker(noise,True),'within_terrain_flicker_limit')
        image[::2,:16,:32]+=.006
        coherent=detector.coherent_blocks(image,valid)['16']['mae']
        self.assertEqual(detector.classify_flicker(coherent,True),'coherent_hillside_flicker')

    def test_camera_motion_is_not_accepted_as_flicker_evidence(self):
        self.assertEqual(detector.classify_flicker(.08,False),'not_applicable_to_this_view_or_motion')

    def test_fixed_roi_requires_the_saved_view_and_resolution(self):
        def packed(a): return {f'e{r}{c}':float(a[r,c]) for r in range(4) for c in range(4)}
        view=np.eye(4);view[:3,:3]=[[.17250216,.98415828,.04093605],[-.13047659,.06402302,-.98938215],[.97632939,-.16532943,-.13945365]]
        view[2,3]=-728.0894
        projection=np.eye(4);projection[0,0]=.97427857;projection[1,1]=-1.73205078
        frame={'width':2560,'height':1440,'view':packed(view),'gpuProjection':packed(projection)}
        meta={'x':768,'y':896,'width':768,'height':384}
        self.assertTrue(detector.matches_fixture_view(meta,[frame]))
        frame['width']=1920
        self.assertFalse(detector.matches_fixture_view(meta,[frame]))
        frame['width']=2560;view[2,3]=-1000;frame['view']=packed(view)
        self.assertFalse(detector.matches_fixture_view(meta,[frame]))


if __name__=='__main__': unittest.main()
