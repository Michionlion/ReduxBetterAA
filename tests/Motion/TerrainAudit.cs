using System;
using System.Collections.Generic;
using System.IO;
using KSP.Game;
using ReduxBetterAA.Rendering;
using UnityEngine;

namespace ReduxBetterAAVisualTests
{
    public sealed partial class MotionTestMod
    {
        [Serializable] public sealed class SceneAudit
        {
            public string mode,solution,backend,shadowQuality,shadowProjection;
            public float smoothingTime,fixedDelta,shadowDistance;
            public int shadowCascades;
            public string[] bodies,materials,cameras;
        }

        private void WriteTerrainAudit(string label)
        {
            if(label=="." || label==".." || string.IsNullOrEmpty(label) || label.IndexOfAny(Path.GetInvalidFileNameChars())>=0)
                throw new ArgumentException("Invalid audit label");
            var game=GameManager.Instance.Game;
            var rig=game.CameraManager.FlightCamera;
            var solution=rig.ActiveSolution;
            var tweakables=solution.GetType().GetProperty("Tweakables",Any)?.GetValue(solution,null);
            var smoothing=tweakables?.GetType().GetField("cinematicInputSmoothingTime",Any)?.GetValue(tweakables);
            var vessel=game.ViewController.GetBehaviorIfLoaded(game.ViewController.GetActiveSimVessel(true));
            var bodies=new List<string>();
            foreach(var body in vessel.GetComponentsInChildren<Rigidbody>(true))
                bodies.Add(body.name+" | interpolation="+body.interpolation+" | kinematic="+body.isKinematic);
            var camera=TemporalCameraDiscovery.Discover().ResolveCamera;
            var materials=new List<string>();
            foreach(var renderer in UnityEngine.Object.FindObjectsByType<Renderer>())
            {
                if(!renderer.enabled || !renderer.gameObject.activeInHierarchy || Vector3.Distance(renderer.bounds.center,camera.transform.position)>1500) continue;
                foreach(var material in renderer.sharedMaterials)
                {
                    if(material==null) continue;
                    string description=renderer.name+" | "+material.name+" | "+material.shader.name+" | queue="+material.renderQueue+" | bounds="+renderer.bounds;
                    foreach(string property in new[]{"_ZWrite","_ZTest","_OffsetFactor","_OffsetUnits","_Cull"})
                        if(material.HasProperty(property)) description+=" | "+property+"="+material.GetFloat(property);
                    materials.Add(description);
                }
            }
            var cameras=new List<string>();
            foreach(var c in Camera.allCameras)
                cameras.Add(c.name+" | depth="+c.depth+" | near="+c.nearClipPlane+" | far="+c.farClipPlane+" | transparentJitter="+c.useJitteredProjectionMatrixForTransparentRendering);
            var audit=new SceneAudit {mode=rig.Mode.ToString(),solution=solution.GetType().FullName,backend=TemporalCoordinator.Current.SelectedBackend,
                smoothingTime=smoothing==null?-1:Convert.ToSingle(smoothing),fixedDelta=Time.fixedDeltaTime,
                shadowQuality=QualitySettings.shadows.ToString(),shadowProjection=QualitySettings.shadowProjection.ToString(),
                shadowDistance=QualitySettings.shadowDistance,shadowCascades=QualitySettings.shadowCascades,
                bodies=bodies.ToArray(),materials=materials.ToArray(),cameras=cameras.ToArray()};
            File.WriteAllText(Path.Combine(_root,label+"-scene-audit.json"),JsonUtility.ToJson(audit,true));
        }
    }
}
