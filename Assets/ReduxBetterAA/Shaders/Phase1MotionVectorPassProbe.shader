// Diagnostic copy of Unity 6000.4.1f1's Hidden/Internal-MotionVectors shader.
// The original source is MIT-licensed by Unity Technologies. This variant is
// only installed temporarily by TestHarness investigations and is never part
// of a production AA backend.
Shader "Hidden/ReduxBetterAA/Phase1MotionVectorPassProbe"
{
    SubShader
    {
        CGINCLUDE
        #include "UnityCG.cginc"

        #if defined(USING_STEREO_MATRICES)
            float4x4 _StereoNonJitteredVP[2];
            float4x4 _StereoPreviousVP[2];
        #else
            float4x4 _NonJitteredVP;
            float4x4 _PreviousVP;
        #endif
        float4x4 _PreviousM;
        bool _HasLastPositionData;
        bool _ForceNoMotion;
        float _MotionVectorDepthBias;

        int _ReduxBetterAAMotionProbeMode;
        float4x4 _ReduxBetterAAManagedPreviousVP;

        struct MotionVectorData
        {
            float4 transferPos : TEXCOORD0;
            float4 transferPosOld : TEXCOORD1;
            nointerpolation float invalidObjectHistory : TEXCOORD2;
            float4 pos : SV_POSITION;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        struct MotionVertexInput
        {
            float4 vertex : POSITION;
            float3 oldPos : TEXCOORD4;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        MotionVectorData VertMotionVectors(MotionVertexInput v)
        {
            MotionVectorData o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            o.pos = UnityObjectToClipPos(v.vertex);

            #if defined(UNITY_REVERSED_Z)
                o.pos.z -= _MotionVectorDepthBias * o.pos.w;
            #else
                o.pos.z += _MotionVectorDepthBias * o.pos.w;
            #endif

            float4 currentWorld = mul(unity_ObjectToWorld, v.vertex);
            float4 previousVertex = _HasLastPositionData
                ? float4(v.oldPos, 1)
                : v.vertex;
            float4 previousWorld = mul(_PreviousM, previousVertex);
            float previousIdentityError =
                abs(_PreviousM[0][0] - 1.0) + abs(_PreviousM[0][1]) +
                abs(_PreviousM[0][2]) + abs(_PreviousM[0][3]) +
                abs(_PreviousM[1][0]) + abs(_PreviousM[1][1] - 1.0) +
                abs(_PreviousM[1][2]) + abs(_PreviousM[1][3]) +
                abs(_PreviousM[2][0]) + abs(_PreviousM[2][1]) +
                abs(_PreviousM[2][2] - 1.0) + abs(_PreviousM[2][3]) +
                abs(_PreviousM[3][0]) + abs(_PreviousM[3][1]) +
                abs(_PreviousM[3][2]) + abs(_PreviousM[3][3] - 1.0);
            float3 currentTranslation = float3(
                unity_ObjectToWorld[0][3],
                unity_ObjectToWorld[1][3],
                unity_ObjectToWorld[2][3]);
            o.invalidObjectHistory =
                previousIdentityError < 0.0001 &&
                distance(currentTranslation, _WorldSpaceCameraPos.xyz) < 0.5
                    ? 1.0
                    : 0.0;
            if (_ReduxBetterAAMotionProbeMode == 9)
            {
                // Control: current vertex transformed by the current object matrix.
                previousWorld = currentWorld;
            }
            else if (_ReduxBetterAAMotionProbeMode == 10)
            {
                // Preserve _PreviousM but bypass the previous-position stream.
                previousWorld = mul(_PreviousM, v.vertex);
            }
            else if (_ReduxBetterAAMotionProbeMode == 11)
            {
                // Preserve the previous-position stream but bypass _PreviousM.
                previousWorld = mul(unity_ObjectToWorld, previousVertex);
            }

            #if defined(USING_STEREO_MATRICES)
                o.transferPos = mul(
                    _StereoNonJitteredVP[unity_StereoEyeIndex],
                    currentWorld);
                o.transferPosOld = mul(
                    _StereoPreviousVP[unity_StereoEyeIndex],
                    previousWorld);
            #else
                o.transferPos = mul(
                    _NonJitteredVP,
                    currentWorld);
                o.transferPosOld = mul(
                    _PreviousVP,
                    previousWorld);
            #endif
            return o;
        }

        inline half2 EncodeRows(
            float4x4 matrixToEncode,
            float2 uv,
            bool lowerRows)
        {
            bool right = uv.x >= 0.5;
            bool top = uv.y >= 0.5;
            int row = lowerRows ? (top ? 3 : 2) : (top ? 1 : 0);
            return right
                ? half2(matrixToEncode[row][2], matrixToEncode[row][3])
                : half2(matrixToEncode[row][0], matrixToEncode[row][1]);
        }

        inline half2 EncodeMatrixRows(float2 uv, bool lowerRows)
        {
            #if defined(USING_STEREO_MATRICES)
                float4x4 matrixToEncode = _StereoPreviousVP[unity_StereoEyeIndex];
            #else
                float4x4 matrixToEncode = _PreviousVP;
            #endif
            return EncodeRows(matrixToEncode, uv, lowerRows);
        }

        inline half2 EncodeCurrentMatrixRows(float2 uv, bool lowerRows)
        {
            #if defined(USING_STEREO_MATRICES)
                float4x4 matrixToEncode =
                    _StereoNonJitteredVP[unity_StereoEyeIndex];
            #else
                float4x4 matrixToEncode = _NonJitteredVP;
            #endif
            return EncodeRows(matrixToEncode, uv, lowerRows);
        }

        half4 FragMotionVectors(MotionVectorData i) : SV_Target
        {
            float2 screenUv = i.pos.xy / _ScreenParams.xy;
            if (_ReduxBetterAAMotionProbeMode == 17)
            {
                // Keep the full-screen camera pass and discard every object pass.
                clip(-1);
            }
            if (_ReduxBetterAAMotionProbeMode == 18 &&
                i.invalidObjectHistory > 0.5)
            {
                // Discard only camera-centred indirect draws whose previous
                // object transform was injected as identity by Unity.
                clip(-1);
            }
            if (_ReduxBetterAAMotionProbeMode == 1)
            {
                // Negative X identifies pixels overwritten by the object pass.
                return half4(-0.25, 0, 0, 1);
            }
            if (_ReduxBetterAAMotionProbeMode == 5)
                return half4(EncodeMatrixRows(screenUv, false), 0, 1);
            if (_ReduxBetterAAMotionProbeMode == 6)
                return half4(EncodeMatrixRows(screenUv, true), 0, 1);
            if (_ReduxBetterAAMotionProbeMode == 7)
                return half4(EncodeCurrentMatrixRows(screenUv, false), 0, 1);
            if (_ReduxBetterAAMotionProbeMode == 8)
                return half4(EncodeCurrentMatrixRows(screenUv, true), 0, 1);
            if (_ReduxBetterAAMotionProbeMode == 12)
            {
                half tag = _HasLastPositionData ? 0.25 : -0.25;
                return half4(tag, tag, 0, 1);
            }
            if (_ReduxBetterAAMotionProbeMode == 13)
                return half4(EncodeRows(_PreviousM, screenUv, false), 0, 1);
            if (_ReduxBetterAAMotionProbeMode == 14)
                return half4(EncodeRows(_PreviousM, screenUv, true), 0, 1);
            if (_ReduxBetterAAMotionProbeMode == 15)
                return half4(
                    EncodeRows(unity_ObjectToWorld, screenUv, false),
                    0,
                    1);
            if (_ReduxBetterAAMotionProbeMode == 16)
                return half4(
                    EncodeRows(unity_ObjectToWorld, screenUv, true),
                    0,
                    1);

            float3 hPos = i.transferPos.xyz / i.transferPos.w;
            float3 hPosOld = i.transferPosOld.xyz / i.transferPosOld.w;
            float2 vPos = (hPos.xy + 1.0) * 0.5;
            float2 vPosOld = (hPosOld.xy + 1.0) * 0.5;
            #if UNITY_UV_STARTS_AT_TOP
                vPos.y = 1.0 - vPos.y;
                vPosOld.y = 1.0 - vPosOld.y;
            #endif
            half2 uvDiff = vPos - vPosOld;
            return lerp(half4(uvDiff, 0, 1), 0, (half)_ForceNoMotion);
        }

        UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

        struct CamMotionVectors
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
            float3 ray : TEXCOORD1;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        struct CamMotionVectorsInput
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        CamMotionVectors VertMotionVectorsCamera(CamMotionVectorsInput v)
        {
            CamMotionVectors o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            o.pos = UnityObjectToClipPos(v.vertex);
            #ifdef UNITY_HALF_TEXEL_OFFSET
                o.pos.xy += (_ScreenParams.zw - 1.0) *
                    float2(-1, 1) * o.pos.w;
            #endif
            o.uv = ComputeScreenPos(o.pos);
            o.ray = v.normal;
            return o;
        }

        inline half2 CalculateMotionWithPrevious(
            float rawDepth,
            float3 inRay,
            float4x4 previousVP)
        {
            float depth = Linear01Depth(rawDepth);
            float3 ray = inRay * (_ProjectionParams.z / inRay.z);
            float3 viewPos = ray * depth;
            float4 worldPos = mul(
                unity_CameraToWorld,
                float4(viewPos, 1.0));
            float4 prevClipPos = mul(previousVP, worldPos);
            #if defined(USING_STEREO_MATRICES)
                float4 curClipPos = mul(
                    _StereoNonJitteredVP[unity_StereoEyeIndex],
                    worldPos);
            #else
                float4 curClipPos = mul(_NonJitteredVP, worldPos);
            #endif
            float2 prevHPos = prevClipPos.xy / prevClipPos.w;
            float2 curHPos = curClipPos.xy / curClipPos.w;
            float2 previousUv = (prevHPos + 1.0) * 0.5;
            float2 currentUv = (curHPos + 1.0) * 0.5;
            #if UNITY_UV_STARTS_AT_TOP
                previousUv.y = 1.0 - previousUv.y;
                currentUv.y = 1.0 - currentUv.y;
            #endif
            return currentUv - previousUv;
        }

        inline half4 ProbeCameraMotion(CamMotionVectors i, float rawDepth)
        {
            float2 screenUv = i.pos.xy / _ScreenParams.xy;
            if (_ReduxBetterAAMotionProbeMode == 1)
            {
                // Positive X identifies the full-screen camera pass.
                return half4(0.25, 0, 0, 1);
            }
            if (_ReduxBetterAAMotionProbeMode == 2)
                return half4(0, 0, 0, 1);
            if (_ReduxBetterAAMotionProbeMode == 5)
                return half4(EncodeMatrixRows(screenUv, false), 0, 1);
            if (_ReduxBetterAAMotionProbeMode == 6)
                return half4(EncodeMatrixRows(screenUv, true), 0, 1);
            if (_ReduxBetterAAMotionProbeMode == 7)
                return half4(EncodeCurrentMatrixRows(screenUv, false), 0, 1);
            if (_ReduxBetterAAMotionProbeMode == 8)
                return half4(EncodeCurrentMatrixRows(screenUv, true), 0, 1);
            if (_ReduxBetterAAMotionProbeMode >= 9 &&
                _ReduxBetterAAMotionProbeMode <= 16)
                return half4(0, 0, 0, 1);

            #if defined(USING_STEREO_MATRICES)
                float4x4 nativePrevious =
                    _StereoPreviousVP[unity_StereoEyeIndex];
            #else
                float4x4 nativePrevious = _PreviousVP;
            #endif
            float4x4 previous = _ReduxBetterAAMotionProbeMode == 3
                ? _ReduxBetterAAManagedPreviousVP
                : nativePrevious;
            half2 motion = CalculateMotionWithPrevious(
                rawDepth,
                i.ray,
                previous);
            if (_ReduxBetterAAMotionProbeMode == 4)
            {
                half2 managedMotion = CalculateMotionWithPrevious(
                    rawDepth,
                    i.ray,
                    _ReduxBetterAAManagedPreviousVP);
                motion -= managedMotion;
            }
            return half4(motion, 0, 1);
        }

        half4 FragMotionVectorsCamera(CamMotionVectors i) : SV_Target
        {
            float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
            return ProbeCameraMotion(i, depth);
        }

        half4 FragMotionVectorsCameraWithDepth(
            CamMotionVectors i,
            out float outDepth : SV_Depth) : SV_Target
        {
            float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
            outDepth = depth;
            return ProbeCameraMotion(i, depth);
        }
        ENDCG

        Pass
        {
            Tags { "LightMode" = "MotionVectors" }
            ZTest LEqual
            Cull Back
            ZWrite Off
            CGPROGRAM
            #pragma vertex VertMotionVectors
            #pragma fragment FragMotionVectors
            ENDCG
        }

        Pass
        {
            ZTest Always
            Cull Off
            ZWrite Off
            CGPROGRAM
            #pragma vertex VertMotionVectorsCamera
            #pragma fragment FragMotionVectorsCamera
            ENDCG
        }

        Pass
        {
            ZTest Always
            Cull Off
            ZWrite On
            CGPROGRAM
            #pragma vertex VertMotionVectorsCamera
            #pragma fragment FragMotionVectorsCameraWithDepth
            ENDCG
        }
    }
    Fallback Off
}
