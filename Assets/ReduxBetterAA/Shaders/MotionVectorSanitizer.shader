Shader "Hidden/ReduxBetterAA/MotionVectorSanitizer"
{
    Properties
    {
        _MainTex ("Raw motion vectors", 2D) = "black" {}
        _InputMotionComponentSign ("Stored input motion signs", Vector) = (1, 1, 1, 1)
        _ResetMotionHistory ("Reset frame motion", Float) = 0
        _DepthRowsReversed ("Depth rows relative to motion", Float) = 0
        _TerrainMotionValid ("Verified procedural terrain motion", Float) = 0
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment Frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _DepthTexture;
            sampler2D _FrameCorruptionTexture;
            sampler2D _TerrainDepthTexture;
            float4 _MainTex_TexelSize;
            float4 _SourceDimensions;
            float _MaximumMotionSquared;
            float _MaximumFallbackMotionSquared;
            float _MaximumCameraDisagreementSquared;
            float2 _MotionComponentSign;
            float2 _InputMotionComponentSign;
            float _ResetMotionHistory;
            float _DepthRowsReversed;
            float2 _CurrentJitter;
            float4x4 _CurrentInverseViewProjection;
            float4x4 _PreviousViewProjection;
            float _MatrixHistoryValid;
            float _CorruptionMinimumSamples;
            float _SanitizationEnabled;
            float _TerrainMotionValid;
            float4x4 _TerrainPreviousWorldFromCurrent;

            float2 SourceUv(float2 uv)
            {
                // Match the source/depth orientation used by the temporal hooks.
                #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0.0)
                    uv.y = 1.0 - uv.y;
                #endif
                return saturate(uv);
            }

            bool MotionIsInvalid(float2 motion)
            {
                return any(motion != motion) || any(abs(motion) > 10000.0);
            }

            float2 CalculateCameraMotion(float2 uv, float rawDepth, out float valid)
            {
                valid = 0.0;
                if (_MatrixHistoryValid < 0.5)
                    return 0.0;

                float deviceDepth = rawDepth;
                #if !defined(UNITY_REVERSED_Z)
                deviceDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
                #endif

                // Depth and raw motion are rasterized with the current projection
                // jitter, while these matrices deliberately exclude jitter.
                float2 currentUv = uv + _CurrentJitter;
                float4 currentClip = float4(
                    currentUv * 2.0 - 1.0,
                    deviceDepth,
                    1.0
                );
                float4 world = mul(_CurrentInverseViewProjection, currentClip);
                if (abs(world.w) < 0.000001)
                    return 0.0;
                world /= world.w;

                float4 previousClip = mul(_PreviousViewProjection, world);
                if (abs(previousClip.w) < 0.000001)
                    return 0.0;
                float2 previousUv = previousClip.xy / previousClip.w * 0.5 + 0.5;
                float2 motion = currentUv - previousUv;
                if (MotionIsInvalid(motion))
                    return 0.0;
                valid = 1.0;
                return motion;
            }

            float2 RepairTerrainMotion(float2 uv, float2 motion)
            {
                if (_TerrainMotionValid < 0.5 || _MatrixHistoryValid < 0.5 || MotionIsInvalid(motion))
                    return motion;
                float2 depthUv = float2(uv.x, _DepthRowsReversed > 0.5 ? 1.0 - uv.y : uv.y);
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_DepthTexture, depthUv);
                float terrainDepth = SAMPLE_DEPTH_TEXTURE(_TerrainDepthTexture, depthUv);
                #if defined(UNITY_REVERSED_Z)
                if (!(rawDepth > 0.0 && rawDepth <= 1.0)) return motion;
                #else
                if (!(rawDepth >= 0.0 && rawDepth < 1.0)) return motion;
                #endif
                // Both depth inputs describe this camera's raster. A relative
                // float tolerance permits sampling roundoff, not a distance mask.
                // Nearer vessel/object pixels and uncovered sky keep their vectors.
                if (!(abs(rawDepth - terrainDepth) <= max(abs(rawDepth) * 0.000002, 0.000000000001)))
                    return motion;
                float cameraValid;
                float2 cameraMotion = CalculateCameraMotion(uv, rawDepth, cameraValid);
                float2 dimensions = max(_SourceDimensions.xy, 1.0.xx);
                float2 cameraPixels = cameraMotion * dimensions;
                // Unity RGHalf rounding scales with the camera-motion magnitude.
                // Preserve existing object vectors instead of replacing every
                // depth-covered sample with a rigid-planet assumption.
                float tolerance = max(0.01, length(cameraPixels) * 0.001);
                if (cameraValid < 0.5 || length((motion - cameraMotion) * dimensions) > tolerance)
                    return motion;
                float deviceDepth = rawDepth;
                #if !defined(UNITY_REVERSED_Z)
                deviceDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
                #endif
                float2 currentUv = uv + _CurrentJitter;
                float4 world = mul(_CurrentInverseViewProjection,
                    float4(currentUv * 2.0 - 1.0, deviceDepth, 1.0));
                float4 priorWorld = mul(_TerrainPreviousWorldFromCurrent, world);
                float4 priorClip = mul(_PreviousViewProjection, priorWorld);
                if (priorClip.w <= 0.000001) return motion;
                float2 repaired = currentUv - (priorClip.xy / priorClip.w * 0.5 + 0.5);
                float2 pixels = repaired * dimensions;
                return MotionIsInvalid(repaired) || dot(pixels, pixels) > _MaximumFallbackMotionSquared
                    ? motion : repaired;
            }

            float4 Frag(v2f_img input) : SV_Target
            {
                float2 uv = SourceUv(input.uv);
                float2 motion = tex2D(_MainTex, uv).rg * _InputMotionComponentSign;
                if (_ResetMotionHistory > 0.5) return float4(0.0, 0.0, 0.0, 1.0);
                if (_SanitizationEnabled < 0.5)
                {
                    // Terrain repair is independent of optional outlier rejection.
                    motion = RepairTerrainMotion(uv, motion);
                    motion *= _MotionComponentSign;
                    return float4(motion, 0.0, 1.0);
                }
                float2 pixelMotion = motion * max(_SourceDimensions.xy, 1.0.xx);
                bool invalid = MotionIsInvalid(motion);
                bool overLimit =
                    dot(pixelMotion, pixelMotion) > _MaximumMotionSquared;
                float fallbackValid;
                float2 depthUv = float2(uv.x, _DepthRowsReversed > 0.5 ? 1.0 - uv.y : uv.y);
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_DepthTexture, depthUv);
                float2 fallback = CalculateCameraMotion(
                    uv,
                    rawDepth,
                    fallbackValid
                );
                float2 fallbackPixels = fallback *
                    max(_SourceDimensions.xy, 1.0.xx);
                bool fallbackUsable = fallbackValid > 0.5 &&
                    dot(fallbackPixels, fallbackPixels) <=
                    _MaximumFallbackMotionSquared;

                // The launchpad failure is a finite radial/quadrant field, so a
                // magnitude-only cutoff cannot catch every affected sample. Static
                // depth-covered scene motion should remain close to camera
                // reprojection. Preserve object motion within a generous 256 px
                // envelope and replace only large disagreement when the camera
                // fallback itself is bounded. The broad acceptance range
                // permits deliberate fast pans; the tighter disagreement test is
                // what identifies the launchpad field below that magnitude.
                float2 disagreementPixels = pixelMotion - fallbackPixels;
                bool cameraDisagreement = fallbackValid > 0.5 &&
                    dot(disagreementPixels, disagreementPixels) >
                    _MaximumCameraDisagreementSquared;
                bool unverifiedOverLimit = overLimit &&
                    (fallbackValid < 0.5 || cameraDisagreement);
                bool frameCorrupt =
                    tex2D(_FrameCorruptionTexture, 0.5.xx).r > 0.5;
                if (frameCorrupt || invalid || unverifiedOverLimit ||
                    cameraDisagreement)
                {
                    motion = fallbackUsable ? fallback : 0.0;
                }

                motion = RepairTerrainMotion(uv, motion);
                // The managed vendor APIs now use positive pixel scales. These
                // explicit component signs are the only motion-direction controls.
                motion *= _MotionComponentSign;
                return float4(motion, 0.0, 1.0);
            }
            ENDCG
        }


        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragClassify
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _DepthTexture;
            float4 _MainTex_TexelSize;
            float4 _SourceDimensions;
            float _MaximumMotionSquared;
            float _MaximumCameraDisagreementSquared;
            float2 _CurrentJitter;
            float2 _InputMotionComponentSign;
            float _DepthRowsReversed;
            float4x4 _CurrentInverseViewProjection;
            float4x4 _PreviousViewProjection;
            float _MatrixHistoryValid;
            float _CorruptionMinimumSamples;

            float2 SourceUv(float2 uv)
            {
                #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0.0)
                    uv.y = 1.0 - uv.y;
                #endif
                return saturate(uv);
            }

            bool MotionIsInvalid(float2 motion)
            {
                return any(motion != motion) || any(abs(motion) > 10000.0);
            }

            float2 CalculateCameraMotion(float2 uv, float rawDepth, out float valid)
            {
                valid = 0.0;
                if (_MatrixHistoryValid < 0.5)
                    return 0.0;

                float deviceDepth = rawDepth;
                #if !defined(UNITY_REVERSED_Z)
                deviceDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
                #endif

                float2 currentUv = uv + _CurrentJitter;
                float4 currentClip = float4(
                    currentUv * 2.0 - 1.0,
                    deviceDepth,
                    1.0
                );
                float4 world = mul(_CurrentInverseViewProjection, currentClip);
                if (abs(world.w) < 0.000001)
                    return 0.0;
                world /= world.w;

                float4 previousClip = mul(_PreviousViewProjection, world);
                if (abs(previousClip.w) < 0.000001)
                    return 0.0;
                float2 previousUv = previousClip.xy / previousClip.w * 0.5 + 0.5;
                float2 motion = currentUv - previousUv;
                if (MotionIsInvalid(motion))
                    return 0.0;
                valid = 1.0;
                return motion;
            }

            float SuspiciousMotionSample(float2 uv)
            {
                uv = SourceUv(uv);
                float2 motion = tex2D(_MainTex, uv).rg * _InputMotionComponentSign;
                float2 pixelMotion = motion * max(_SourceDimensions.xy, 1.0.xx);
                if (MotionIsInvalid(motion))
                    return 1.0;

                float fallbackValid;
                float2 depthUv = float2(uv.x, _DepthRowsReversed > 0.5 ? 1.0 - uv.y : uv.y);
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_DepthTexture, depthUv);
                float2 fallback = CalculateCameraMotion(
                    uv,
                    rawDepth,
                    fallbackValid
                );
                float2 fallbackPixels = fallback *
                    max(_SourceDimensions.xy, 1.0.xx);
                float2 disagreementPixels = pixelMotion - fallbackPixels;
                bool cameraDisagreement = fallbackValid > 0.5 &&
                    dot(disagreementPixels, disagreementPixels) >
                    _MaximumCameraDisagreementSquared;
                bool overLimit =
                    dot(pixelMotion, pixelMotion) > _MaximumMotionSquared;
                bool unverifiedOverLimit = overLimit && fallbackValid < 0.5;
                return cameraDisagreement || unverifiedOverLimit ? 1.0 : 0.0;
            }

            float4 FragClassify(v2f_img input) : SV_Target
            {
                float suspicious = 0.0;
                suspicious += SuspiciousMotionSample(float2(0.125, 0.125));
                suspicious += SuspiciousMotionSample(float2(0.375, 0.125));
                suspicious += SuspiciousMotionSample(float2(0.625, 0.125));
                suspicious += SuspiciousMotionSample(float2(0.875, 0.125));
                suspicious += SuspiciousMotionSample(float2(0.125, 0.375));
                suspicious += SuspiciousMotionSample(float2(0.375, 0.375));
                suspicious += SuspiciousMotionSample(float2(0.625, 0.375));
                suspicious += SuspiciousMotionSample(float2(0.875, 0.375));
                suspicious += SuspiciousMotionSample(float2(0.125, 0.625));
                suspicious += SuspiciousMotionSample(float2(0.375, 0.625));
                suspicious += SuspiciousMotionSample(float2(0.625, 0.625));
                suspicious += SuspiciousMotionSample(float2(0.875, 0.625));
                suspicious += SuspiciousMotionSample(float2(0.125, 0.875));
                suspicious += SuspiciousMotionSample(float2(0.375, 0.875));
                suspicious += SuspiciousMotionSample(float2(0.625, 0.875));
                suspicious += SuspiciousMotionSample(float2(0.875, 0.875));
                float corrupt = suspicious >= _CorruptionMinimumSamples
                    ? 1.0
                    : 0.0;
                return float4(corrupt, suspicious / 16.0, 0.0, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
