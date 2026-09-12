import importlib.util
from pathlib import Path
import unittest
import numpy as np

spec=importlib.util.spec_from_file_location("motion",Path(__file__).resolve().parents[2]/"tools/analyze-motion-series.py")
motion=importlib.util.module_from_spec(spec);spec.loader.exec_module(motion)


class MotionDetectionTests(unittest.TestCase):
    def test_120hz_observation_of_valid_50hz_steps_is_not_bad_vectors(self):
        time=np.arange(600)/120
        physics=np.floor(time/.02+1e-8)
        step=np.r_[False,np.diff(physics)>0]
        magnitude=np.where(step,4.0,.00001)
        self.assertEqual(motion.classify_cadence(magnitude,step,.001),"physics_stepped_valid_camera_motion")

    def test_disagreement_changes_diagnosis(self):
        step=np.arange(600)%2==0
        magnitude=np.where(step,4.0,.00001)
        self.assertEqual(motion.classify_cadence(magnitude,step,.5),"physics_stepped_check_vector_coherence")

    def test_constant_velocity_and_paused_control(self):
        step=np.arange(600)%2==0
        self.assertEqual(motion.classify_cadence(np.ones(600),step,.001),"motion_not_dominated_by_physics_steps")
        self.assertEqual(motion.classify_cadence(np.zeros(600),step,.001),"stationary_or_insufficient_motion")

    def test_low_rate_cannot_certify_absence_of_step_flicker(self):
        self.assertEqual(motion.classify_cadence(np.ones(60),np.ones(60,bool),.001),"insufficient_inter_tick_samples")

    def test_zero_motion_disagreeing_with_camera_is_not_a_stationary_pass(self):
        self.assertEqual(motion.classify_cadence(np.zeros(60),np.arange(60)%2==0,.5),"missing_motion_check_vector_coherence")

    def test_metadata_rejects_missing_duplicate_fallback_and_pending(self):
        meta={"count":8,"backend":"NVIDIA DLAA","pending":0}
        frames=[dict(frame=i,realtime=i/120,backend="NVIDIA DLAA") for i in range(8)]
        motion.validate_frames(meta,frames)
        with self.assertRaisesRegex(ValueError,"Missing"): motion.validate_frames(meta,frames[:-1])
        for key,value,pattern in (("frame",0,"gap"),("realtime",0,"timestamp"),("backend","Off","fallback")):
            copy=[dict(r) for r in frames];copy[1][key]=value
            with self.assertRaisesRegex(ValueError,pattern): motion.validate_frames(meta,copy)
        with self.assertRaisesRegex(ValueError,"readback"): motion.validate_frames(dict(meta,pending=1),frames)

    def test_nonfinite_readbacks_cannot_pass(self):
        motion.require_finite(np.zeros(3))
        for value in (np.nan,np.inf,-np.inf):
            with self.assertRaisesRegex(ValueError,"Nonfinite"): motion.require_finite(np.array([value]))

    def test_ema_audit_rejects_wrong_sign_or_unfiltered_output(self):
        raw=np.zeros((20,2,2,2));raw[::2]=2
        filtered=np.zeros_like(raw)
        for i in range(1,len(raw)): filtered[i]=.5*(-raw[i]+filtered[i-1])
        valid=np.ones(19,bool);region=np.ones((2,2),bool)
        self.assertAlmostEqual(motion.ema_error(raw,filtered,[-1,-1],valid,region),0)
        self.assertGreater(motion.ema_error(raw,filtered,[1,1],valid,region),.5)
        self.assertGreater(motion.ema_error(raw,-raw,[-1,-1],valid,region),.5)


if __name__=="__main__": unittest.main()
