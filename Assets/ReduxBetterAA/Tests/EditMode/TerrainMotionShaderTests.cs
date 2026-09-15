using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ReduxBetterAA.Tests
{
    public sealed class TerrainMotionShaderTests
    {
        private const int Width = 64, Height = 32;

        [TestCase(false, false, 1, 1, true)]
        [TestCase(true, false, -1, 1, true)]
        [TestCase(false, true, 1, -1, true)]
        [TestCase(true, true, -1, -1, true)]
        [TestCase(false, false, -1, -1, false)]
        [TestCase(true, true, 1, 1, false)]
        public void RepairsOnlyVerifiedTerrainAndPreservesOccludersObjectMotionAndSky(
            bool outlierRejection, bool renderTextureProjection, int signX, int signY, bool historyValid)
        {
            var go = new GameObject("Terrain motion projection test");
            var camera = go.AddComponent<Camera>(); camera.enabled = false;
            camera.nearClipPlane = .3f; camera.farClipPlane = 100; camera.aspect = (float)Width / Height;
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/ReduxBetterAA/Shaders/MotionVectorSanitizer.shader");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            var motion = new Texture2D(Width, Height, TextureFormat.RGBAHalf, false, true) { filterMode = FilterMode.Point };
            var sceneDepth = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point };
            var terrainDepth = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point };
            var output = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            output.Create();
            var jitter = new Vector2(.3125f / Width, -.21875f / Height);
            var projection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, renderTextureProjection);
            var current = projection * camera.worldToCameraMatrix;
            var previous = current * Matrix4x4.TRS(new Vector3(.006f, -.004f, .002f), Quaternion.Euler(.15f, -.25f, .1f), Vector3.one);
            var inverse = current.inverse;
            var planetCurrent = Matrix4x4.TRS(new Vector3(5, -7, 3), Quaternion.Euler(4, 2, 1), Vector3.one);
            var planetPrevious = Matrix4x4.TRS(new Vector3(5.012f, -7.009f, 3.002f), Quaternion.Euler(4.02f, 2.03f, 1), Vector3.one);
            var priorWorldFromCurrent = planetPrevious * planetCurrent.inverse;
            var raw = new Color[Width * Height]; var z = new Color[raw.Length]; var terrain = new Color[raw.Length];
            var expected = new Vector2[raw.Length];
            float largestRepairPixels = 0;
            for (int y = 0; y < Height; ++y) for (int x = 0; x < Width; ++x)
            {
                int index = y * Width + x, region = x / 16;
                float terrainZ = .12f + .08f * y / Height;
                float sceneZ = region == 1 ? .75f : region == 3 ? (SystemInfo.usesReversedZBuffer ? 0 : 1) : terrainZ;
                z[index] = new Color(sceneZ, 0, 0, 1); terrain[index] = new Color(terrainZ, 0, 0, 1);
                var uv = new Vector2((x + .5f) / Width, (y + .5f) / Height) + jitter;
                float deviceZ = SystemInfo.usesReversedZBuffer ? sceneZ : Mathf.Lerp(-1, 1, sceneZ);
                // World position and previous body-local position provide the
                // independent geometric reference for this moving rigid surface.
                var world = inverse * new Vector4(uv.x * 2 - 1, uv.y * 2 - 1, deviceZ, 1);
                var oldStatic = previous * world;
                var cameraMotion = uv - new Vector2(oldStatic.x / oldStatic.w, oldStatic.y / oldStatic.w) * .5f - Vector2.one * .5f;
                var oldBodyLocal = planetCurrent.inverse * world;
                var oldMoving = previous * (planetPrevious * oldBodyLocal);
                var bodyMotion = uv - new Vector2(oldMoving.x / oldMoving.w, oldMoving.y / oldMoving.w) * .5f - Vector2.one * .5f;
                var input = region == 3 ? Vector2.zero : cameraMotion;
                if (region == 1 || region == 2) input += new Vector2(2f / Width, -1f / Height);
                raw[index] = new Color(input.x, input.y, 0, 1);
                var result = region == 0 && historyValid ? bodyMotion : input;
                expected[index] = new Vector2(result.x * signX, result.y * signY);
                if (region == 0) largestRepairPixels = Mathf.Max(largestRepairPixels, (bodyMotion - cameraMotion).magnitude * Height);
            }
            Assert.That(largestRepairPixels, Is.GreaterThan(.1f), "Fixture must require a measurable object-motion correction");
            motion.SetPixels(raw); motion.Apply(); sceneDepth.SetPixels(z); sceneDepth.Apply(); terrainDepth.SetPixels(terrain); terrainDepth.Apply();
            material.SetTexture("_DepthTexture", sceneDepth); material.SetTexture("_TerrainDepthTexture", terrainDepth);
            material.SetTexture("_FrameCorruptionTexture", Texture2D.blackTexture);
            material.SetVector("_SourceDimensions", new Vector4(Width, Height, 1f / Width, 1f / Height));
            material.SetVector("_CurrentJitter", jitter); material.SetVector("_MotionComponentSign", new Vector4(signX, signY, 0, 0));
            material.SetMatrix("_CurrentInverseViewProjection", inverse); material.SetMatrix("_PreviousViewProjection", previous);
            material.SetMatrix("_TerrainPreviousWorldFromCurrent", priorWorldFromCurrent);
            material.SetFloat("_MatrixHistoryValid", historyValid ? 1 : 0); material.SetFloat("_TerrainMotionValid", 1);
            material.SetFloat("_SanitizationEnabled", outlierRejection ? 1 : 0);
            material.SetFloat("_MaximumMotionSquared", 256 * 256); material.SetFloat("_MaximumFallbackMotionSquared", 256 * 256);
            material.SetFloat("_MaximumCameraDisagreementSquared", 96 * 96);
            var oldTarget = RenderTexture.active;
            var readback = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true);
            try
            {
                Graphics.Blit(motion, output, material, 0);
                RenderTexture.active = output;
                readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); readback.Apply();
                var actual = readback.GetPixels();
                for (int y = 1; y < Height - 1; ++y) for (int x = 1; x < Width - 1; ++x)
                {
                    int index = y * Width + x;
                    Assert.That(Mathf.Abs(actual[index].r - expected[index].x) * Width, Is.LessThan(.025f), "X at " + x + "," + y);
                    Assert.That(Mathf.Abs(actual[index].g - expected[index].y) * Height, Is.LessThan(.025f), "Y at " + x + "," + y);
                }
            }
            finally
            {
                RenderTexture.active = oldTarget;
                Object.DestroyImmediate(readback); Object.DestroyImmediate(output); Object.DestroyImmediate(material);
                Object.DestroyImmediate(motion); Object.DestroyImmediate(sceneDepth); Object.DestroyImmediate(terrainDepth); Object.DestroyImmediate(go);
            }
        }
    }
}
