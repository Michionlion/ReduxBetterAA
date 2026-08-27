Shader "Hidden/ReduxBetterAA/Phase1BufferDebug"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _MotionScale ("Raw motion display scale", Float) = 32.0
        _MotionQuietPixels ("Quiet motion threshold in pixels", Float) = 0.1
        _MotionOutlierPixels ("Outlier motion threshold in pixels", Float) = 64.0
        _DiagnosticPixelDimensions ("Diagnostic source dimensions", Vector) = (1920, 1080, 0, 0)
        _MotionComponentSign ("Motion component sign", Vector) = (-1, -1, 0, 0)
        _SanitizedMotionComponentSign ("Sanitized motion component sign", Vector) = (-1, -1, 0, 0)
        _SanitizedMotionTexture ("Sanitized vendor motion", 2D) = "black" {}
        _MotionCorruptionTexture ("Motion corruption flag", 2D) = "black" {}
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        sampler2D _CameraDepthTexture;
        sampler2D _CameraMotionVectorsTexture;
        sampler2D _SanitizedMotionTexture;
        sampler2D _MotionCorruptionTexture;
        float4 _MainTex_TexelSize;
        float _MotionScale;
        float _MotionQuietPixels;
        float _MotionOutlierPixels;
        float4 _DiagnosticPixelDimensions;
        float2 _MotionComponentSign;
        float2 _SanitizedMotionComponentSign;
        float2 _CurrentJitter;
        float4x4 _CurrentInverseViewProjection;
        float4x4 _PreviousViewProjection;
        float _MatrixHistoryValid;

        float2 DiagnosticUv(float2 uv)
        {
            // Command-buffer blits may invert the source on D3D-like platforms.
            #if UNITY_UV_STARTS_AT_TOP
            if (_MainTex_TexelSize.y < 0.0)
                uv.y = 1.0 - uv.y;
            #endif
            return saturate(uv);
        }

        bool MotionIsInvalid(float2 motion)
        {
            // NaNs fail self-equality. Values this large cannot be valid screen-UV motion.
            return any(motion != motion) || any(abs(motion) > 10000.0);
        }

        float2 SafeMotion(float2 uv)
        {
            float2 motion = tex2D(_CameraMotionVectorsTexture, uv).rg;
            if (MotionIsInvalid(motion))
                return 0.0;
            return motion;
        }

        float MotionPixels(float2 motion)
        {
            return length(motion * max(_DiagnosticPixelDimensions.xy, 1.0.xx));
        }

        float LinearDiagnosticDepth(float2 uv)
        {
            float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
            return Linear01Depth(rawDepth);
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

            // The sampled depth was rasterized with the active projection
            // jitter; the diagnostic matrices deliberately exclude it.
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

        float3 HsvToRgb(float3 hsv)
        {
            float3 p = abs(frac(hsv.xxx + float3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
            return hsv.z * lerp(1.0.xxx, saturate(p - 1.0), hsv.y);
        }

        fixed4 FragFinal(v2f_img input) : SV_Target
        {
            return tex2D(_MainTex, DiagnosticUv(input.uv));
        }

        fixed4 FragDepth(v2f_img input) : SV_Target
        {
            float2 uv = DiagnosticUv(input.uv);
            float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
            float linearDepth = saturate(Linear01Depth(rawDepth));
            return float4(linearDepth.xxx, 1.0);
        }

        fixed4 FragDepthDeJittered(v2f_img input) : SV_Target
        {
            float2 uv = DiagnosticUv(input.uv);
            float2 sourceUv = saturate(uv - _CurrentJitter);
            float rawDepth = SAMPLE_DEPTH_TEXTURE(
                _CameraDepthTexture,
                sourceUv
            );
            float linearDepth = saturate(Linear01Depth(rawDepth));
            return float4(linearDepth.xxx, 1.0);
        }

        fixed4 FragMotionRaw(v2f_img input) : SV_Target
        {
            float2 motion = SafeMotion(DiagnosticUv(input.uv));
            return float4(saturate(motion * _MotionScale + 0.5), 0.5, 1.0);
        }

        fixed4 FragMotionNormalized(v2f_img input) : SV_Target
        {
            float2 motion = SafeMotion(DiagnosticUv(input.uv));
            float magnitude = length(motion);
            float pixelMagnitude = MotionPixels(motion);
            float visibleStrength = saturate(
                (pixelMagnitude - _MotionQuietPixels) /
                max(1.0 - _MotionQuietPixels, 1e-5)
            );
            float2 direction = magnitude > 1e-7 ? motion / magnitude : 0.0;
            float2 encodedDirection = lerp(0.5.xx, direction * 0.5 + 0.5, visibleStrength);
            return float4(encodedDirection, saturate(pixelMagnitude / _MotionOutlierPixels), 1.0);
        }

        fixed4 FragMotionMagnitudeAngle(v2f_img input) : SV_Target
        {
            float2 motion = SafeMotion(DiagnosticUv(input.uv));
            float magnitude = saturate(length(motion) * _MotionScale);
            float angle = atan2(motion.y, motion.x) / (2.0 * UNITY_PI) + 0.5;
            return float4(HsvToRgb(float3(angle, magnitude > 1e-6 ? 1.0 : 0.0, magnitude)), 1.0);
        }

        fixed4 FragContribution(v2f_img input) : SV_Target
        {
            float2 uv = DiagnosticUv(input.uv);
            float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
            float linearDepth = Linear01Depth(rawDepth);
            float coverage = linearDepth < 0.99999 ? 1.0 : 0.0;
            return float4(coverage, coverage * 0.25, 1.0 - coverage, 1.0);
        }

        fixed4 FragMotionValidity(v2f_img input) : SV_Target
        {
            float2 uv = DiagnosticUv(input.uv);
            float2 motion = tex2D(_CameraMotionVectorsTexture, uv).rg;
            float linearDepth = LinearDiagnosticDepth(uv);

            // The categorical colors intentionally prioritize buffer coherence over direction.
            // Blue: no depth/quiet. Magenta: no depth/moving. Green: depth/quiet.
            // Cyan: depth/moving. Yellow: depth-covered outlier.
            // Red: invalid NaN/Inf/extreme value.
            if (MotionIsInvalid(motion))
                return float4(1.0, 0.0, 0.0, 1.0);

            float magnitudePixels = MotionPixels(motion);
            float covered = linearDepth < 0.99999 ? 1.0 : 0.0;
            float moving = magnitudePixels > _MotionQuietPixels ? 1.0 : 0.0;
            float outlier = magnitudePixels > _MotionOutlierPixels ? 1.0 : 0.0;
            float intensity = saturate(log2(1.0 + magnitudePixels) /
                                       log2(1.0 + _MotionOutlierPixels));

            if (covered < 0.5)
                return moving > 0.5
                    ? float4(lerp(float3(0.35, 0.0, 0.45), float3(1.0, 0.0, 1.0), intensity), 1.0)
                    : float4(0.0, 0.12, 0.75, 1.0);
            if (outlier > 0.5)
                return float4(1.0, 0.85, 0.0, 1.0);
            return moving > 0.5
                ? float4(lerp(float3(0.0, 0.35, 0.15), float3(0.0, 1.0, 1.0), intensity), 1.0)
                : float4(0.0, 0.35, 0.05, 1.0);
        }

        fixed4 FragMotionSignAgreement(v2f_img input) : SV_Target
        {
            float2 uv = DiagnosticUv(input.uv);
            float2 rawMotion = tex2D(_CameraMotionVectorsTexture, uv).rg;
            float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
            float cameraValid;
            float2 cameraMotion = CalculateCameraMotion(
                uv,
                rawDepth,
                cameraValid
            );
            if (MotionIsInvalid(rawMotion) || cameraValid < 0.5)
                return float4(0.02, 0.04, 0.16, 1.0);

            // Camera reprojection and Unity's buffer are previous-to-current.
            // Vendor motion is current-to-previous, hence the expected negation.
            float2 correctedPixels = rawMotion * _MotionComponentSign *
                max(_DiagnosticPixelDimensions.xy, 1.0.xx);
            float2 expectedPixels = -cameraMotion *
                max(_DiagnosticPixelDimensions.xy, 1.0.xx);
            int axis = uv.x < 0.5 ? 0 : 1;
            float corrected = axis == 0 ? correctedPixels.x : correctedPixels.y;
            float expected = axis == 0 ? expectedPixels.x : expectedPixels.y;
            if (abs(corrected) < _MotionQuietPixels ||
                abs(expected) < _MotionQuietPixels)
                return float4(0.02, 0.08, 0.24, 1.0);

            float agreement = corrected * expected > 0.0 ? 1.0 : 0.0;
            float magnitudeAgreement = min(abs(corrected), abs(expected)) /
                max(max(abs(corrected), abs(expected)), 0.0001);
            float3 correctColor = lerp(
                float3(0.0, 0.25, 0.05),
                float3(0.0, 1.0, 0.2),
                magnitudeAgreement
            );
            float3 wrongColor = lerp(
                float3(0.3, 0.0, 0.05),
                float3(1.0, 0.0, 0.05),
                magnitudeAgreement
            );
            return float4(lerp(wrongColor, correctColor, agreement), 1.0);
        }

        fixed4 FragSanitizedVendorMotion(v2f_img input) : SV_Target
        {
            float2 uv = DiagnosticUv(input.uv);
            float2 motion = tex2D(_SanitizedMotionTexture, uv).rg;
            if (MotionIsInvalid(motion))
                return float4(1.0, 0.0, 0.0, 1.0);

            float pixelMagnitude = MotionPixels(motion);
            float magnitude = length(motion);
            float visibleStrength = saturate(
                (pixelMagnitude - _MotionQuietPixels) /
                max(1.0 - _MotionQuietPixels, 1e-5)
            );
            float2 direction = magnitude > 1e-7 ? motion / magnitude : 0.0;
            float2 encodedDirection = lerp(
                0.5.xx,
                direction * 0.5 + 0.5,
                visibleStrength
            );
            return float4(
                encodedDirection,
                saturate(pixelMagnitude / _MotionOutlierPixels),
                1.0
            );
        }

        fixed4 FragMotionSanitizerDecision(v2f_img input) : SV_Target
        {
            float2 uv = DiagnosticUv(input.uv);
            float2 rawMotion = tex2D(_CameraMotionVectorsTexture, uv).rg *
                _SanitizedMotionComponentSign;
            float2 sanitized = tex2D(_SanitizedMotionTexture, uv).rg;
            float corrupt = tex2D(_MotionCorruptionTexture, 0.5.xx).r;
            float rawPixels = MotionPixels(rawMotion);
            float sanitizedPixels = MotionPixels(sanitized);
            float differencePixels = MotionPixels(sanitized - rawMotion);

            if (corrupt > 0.5)
            {
                // Dark orange: camera fallback; bright orange-red: rejected zero.
                return sanitizedPixels > _MotionQuietPixels
                    ? float4(1.0, 0.42, 0.0, 1.0)
                    : float4(1.0, 0.08, 0.0, 1.0);
            }
            if (differencePixels <= 0.25)
                return float4(0.0, 0.55, 0.08, 1.0);
            if (sanitizedPixels <= _MotionQuietPixels &&
                rawPixels > _MotionQuietPixels)
                return float4(1.0, 0.0, 0.05, 1.0);
            return float4(1.0, 0.85, 0.0, 1.0);
        }

        ENDCG

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragFinal
            #pragma target 3.0
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragDepth
            #pragma target 3.0
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragMotionRaw
            #pragma target 3.0
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragMotionNormalized
            #pragma target 3.0
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragMotionMagnitudeAngle
            #pragma target 3.0
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragContribution
            #pragma target 3.0
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragMotionValidity
            #pragma target 3.0
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragMotionSignAgreement
            #pragma target 3.0
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragSanitizedVendorMotion
            #pragma target 3.0
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragMotionSanitizerDecision
            #pragma target 3.0
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragDepthDeJittered
            #pragma target 3.0
            ENDCG
        }

    }

    Fallback Off
}
