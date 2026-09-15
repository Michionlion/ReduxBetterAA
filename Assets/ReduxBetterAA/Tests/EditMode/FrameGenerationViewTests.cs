using NUnit.Framework;
using ReduxBetterAA.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Tests
{
    public sealed class FrameGenerationViewTests
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void PerspectiveSupportsBothDepthDirectionsOffCenterLensAndGpuYFlip(bool reversed, bool flipY)
        {
            var projection = Projection(reversed, flipY);
            projection.m02 = .15f;
            projection.m12 = -.08f;
            var worldToView = Matrix4x4.Scale(new Vector3(1, 1, -1)) *
                Matrix4x4.TRS(new Vector3(400, 200, -300), Quaternion.Euler(10, 30, 5), Vector3.one).inverse;
            var previous = Projection(reversed, flipY) *
                Matrix4x4.TRS(new Vector3(398, 200, -300), Quaternion.Euler(10, 28, 5), Vector3.one).inverse;
            AssertValid(View(projection: projection, worldToView: worldToView,
                previousViewProjection: previous, reversed: reversed));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void CameraScalarMetadataMustBeFiniteAndPositive(float invalid)
        {
            AssertInvalid(View(fov: invalid));
            AssertInvalid(View(aspect: invalid));
            AssertInvalid(View(unitsToMeters: invalid));
            AssertInvalid(View(near: invalid));
            AssertInvalid(View(far: invalid));
        }

        [Test]
        public void ProjectionMustAgreeWithDeclaredFovAspectAndDepthRange()
        {
            AssertInvalid(View(fov: Mathf.PI));
            AssertInvalid(View(fov: 60f)); // Degrees must not silently become radians.
            AssertInvalid(View(fov: 1f));
            AssertInvalid(View(aspect: 1f));
            AssertInvalid(View(near: .4f));
            AssertInvalid(View(far: .1f));
            AssertInvalid(View(projection: Projection(false, false), reversed: true));
            AssertInvalid(View(projection: Matrix4x4.Perspective(60f, 2f, .2f, 100000f), reversed: false));
        }

        [Test]
        public void UnknownTransposedAndNonPerspectiveConventionsAreRejected()
        {
            AssertInvalid(View(convention: FrameGenerationMatrixConvention.Unknown));
            AssertInvalid(View(convention: (FrameGenerationMatrixConvention)99));
            AssertInvalid(View(projection: Projection(true, false).transpose));
            AssertInvalid(View(projection: Matrix4x4.Ortho(-2, 2, -1, 1, .2f, 100000f)));
            AssertInvalid(View(projection: Matrix4x4.zero));
            var projection = Projection(true, false);
            projection.m12 = float.NaN;
            AssertInvalid(View(projection: projection));
        }

        [Test]
        public void CameraTransformMustProvideAnOrthonormalBasisAndMatchingViewProjection()
        {
            AssertInvalid(View(worldToView: Matrix4x4.Scale(new Vector3(1, 2, 1))));
            var shear = Matrix4x4.identity;
            shear.m01 = .2f;
            AssertInvalid(View(worldToView: shear));
            var projectiveView = Matrix4x4.identity;
            projectiveView.m30 = .2f;
            AssertInvalid(View(worldToView: projectiveView));
            AssertInvalid(View(currentViewProjection: Matrix4x4.identity));
            var displacedView = Matrix4x4.Translate(new Vector3(100, 0, 0));
            AssertInvalid(View(worldToView: displacedView, currentViewProjection: Projection(true, false)));
            AssertInvalid(View(previousViewProjection: Matrix4x4.zero));
            var invalidPrevious = Matrix4x4.identity;
            invalidPrevious.m03 = float.PositiveInfinity;
            AssertInvalid(View(previousViewProjection: invalidPrevious));
        }

        [Test]
        public void ExplicitUnitScaleDoesNotChangeTheSuppliedCameraGeometry()
        {
            AssertValid(View(unitsToMeters: .01f));
            AssertValid(View(unitsToMeters: 1000f));
        }

        // Exact finite D3D/Vulkan [0,1] perspective fixture; does not depend on
        // the editor's graphics API, so camera-contract tests also run headless.
        private static Matrix4x4 Projection(bool reversed, bool flipY)
        {
            const float near = .2f, far = 100000f;
            float scale = 1f / Mathf.Tan(60f * Mathf.Deg2Rad * .5f);
            var projection = Matrix4x4.zero;
            projection.m00 = scale / 2f;
            projection.m11 = flipY ? -scale : scale;
            projection.m22 = reversed ? near / (far - near) : far / (near - far);
            projection.m23 = reversed ? near * far / (far - near) : near * far / (near - far);
            projection.m32 = -1f;
            return projection;
        }

        internal static FrameGenerationView View(float exposure = 1f, float preExposure = 1f,
            float jitterX = .25f, FrameGenerationColorDomain color = FrameGenerationColorDomain.DisplayLinear,
            float fov = 60f * Mathf.Deg2Rad, float aspect = 2f, float unitsToMeters = 1f,
            float near = .2f, float far = 100000f, bool reversed = true,
            FrameGenerationMatrixConvention convention = FrameGenerationMatrixConvention.UnityColumnVectorGpuProjection,
            Matrix4x4? projection = null, Matrix4x4? worldToView = null,
            Matrix4x4? currentViewProjection = null, Matrix4x4? previousViewProjection = null)
        {
            var p = projection ?? Projection(reversed, false);
            var v = worldToView ?? Matrix4x4.identity;
            return new FrameGenerationView(color, FrameGenerationMotionUnits.NormalizedUv,
                new Vector2(jitterX, -.25f), new Vector2(-1, 1), preExposure, exposure,
                16.67f, near, far, reversed, fov, aspect, unitsToMeters, convention,
                p, v, currentViewProjection ?? p * v, previousViewProjection ?? p * v);
        }

        private static void AssertValid(FrameGenerationView view) => Assert.That(Validate(view), Is.True);
        private static void AssertInvalid(FrameGenerationView view) => Assert.That(Validate(view), Is.False);
        private static bool Validate(FrameGenerationView view)
        {
            var caps = new FrameGenerationCapabilities("Test provider", "fixture", true,
                GraphicsDeviceType.Direct3D11, FrameGenerationColorDomain.DisplayLinear,
                FrameGenerationMotionUnits.NormalizedUv, 1u << 2, true, true);
            return FrameGenerationGate.ValidateView(in view, in caps);
        }
    }
}
