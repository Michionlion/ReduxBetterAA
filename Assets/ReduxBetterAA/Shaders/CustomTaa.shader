Shader "Hidden/ReduxBetterAA/CustomTaa"
{
    Properties
    {
        _MainTex ("Current scene color", 2D) = "white" {}
        _HistoryTex ("Previous resolved color", 2D) = "black" {}
        _HistoryDepthTex ("Previous linear depth", 2D) = "white" {}
        _ReduxBetterAAMotionVectors ("Sanitized motion vectors", 2D) = "black" {}
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        sampler2D _HistoryTex;
        sampler2D _HistoryDepthTex;
        sampler2D _CameraDepthTexture;
        sampler2D _ReduxBetterAAMotionVectors;

        float4 _MainTex_TexelSize;
        float4 _SourceDimensions;
        float2 _Jitter;
        float _StationaryHistory;
        float _MovingHistory;
        float _MotionResponsePixels;
        float _MaximumMotionPixels;
        float _DepthThreshold;
        float _DepthEdgeStability;
        float _VarianceGamma;
        float _ReactiveScale;
        float _Sharpening;
        float _NoDepthHistory;
        float _HistoryValid;
        float _DebugMode;
        float4x4 _CurrentInverseViewProjection;
        float4x4 _PreviousViewProjection;
        float _MatrixHistoryValid;

        float2 ResolveUv(float2 uv)
        {
            // On D3D-like platforms Graphics.Blit may expose a vertically inverted source.
            #if UNITY_UV_STARTS_AT_TOP
            if (_MainTex_TexelSize.y < 0.0)
                uv.y = 1.0 - uv.y;
            #endif
            return saturate(uv);
        }

        float3 RgbToYCoCg(float3 color)
        {
            float co = color.r - color.b;
            float temporary = color.b + co * 0.5;
            float cg = color.g - temporary;
            float y = temporary + cg * 0.5;
            return float3(y, co, cg);
        }

        float3 YCoCgToRgb(float3 color)
        {
            float temporary = color.x - color.z * 0.5;
            float g = color.z + temporary;
            float b = temporary - color.y * 0.5;
            float r = b + color.y;
            return float3(r, g, b);
        }

        float Luminance(float3 color)
        {
            return dot(color, float3(0.2126, 0.7152, 0.0722));
        }

        bool MotionIsInvalid(float2 motion)
        {
            return any(motion != motion) || any(abs(motion) > 2.0);
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

            float4 currentClip = float4(uv * 2.0 - 1.0, deviceDepth, 1.0);
            float4 world = mul(_CurrentInverseViewProjection, currentClip);
            if (abs(world.w) < 0.000001)
                return 0.0;
            world /= world.w;

            float4 previousClip = mul(_PreviousViewProjection, world);
            if (abs(previousClip.w) < 0.000001)
                return 0.0;
            float2 previousUv = previousClip.xy / previousClip.w * 0.5 + 0.5;
            float2 motion = uv - previousUv;
            if (MotionIsInvalid(motion))
                return 0.0;
            valid = 1.0;
            return motion;
        }

        float RelativeDepthDifference(float firstDepth, float secondDepth)
        {
            return abs(firstDepth - secondDepth) /
                max(max(firstDepth, secondDepth), 0.01);
        }

        float HasSceneDepth(float linearDepth)
        {
            return 1.0 - step(0.99999, linearDepth);
        }

        float SameDepthSurface(
            float centerDepth,
            float centerHasDepth,
            float sampleDepth)
        {
            float sampleHasDepth = HasSceneDepth(sampleDepth);
            float sameCoverage = 1.0 - abs(centerHasDepth - sampleHasDepth);
            float sameDepth = 1.0 - step(
                _DepthThreshold,
                RelativeDepthDifference(centerDepth, sampleDepth)
            );
            return sameCoverage * lerp(
                1.0,
                sameDepth,
                centerHasDepth * sampleHasDepth
            );
        }

        float BestHistoryDepthDifference(float2 previousUv, float currentDepth)
        {
            // Sample exact texel centers so a bilinear depth history cannot invent
            // an intermediate surface at a silhouette. The nearest matching depth
            // in this one-pixel footprint tolerates subpixel jitter without reaching
            // far enough to preserve a broad disocclusion trail.
            float2 texel = _SourceDimensions.zw;
            float2 basePosition = floor(previousUv * _SourceDimensions.xy - 0.5);
            float bestDifference = 1e20;
            [unroll]
            for (int y = -1; y <= 1; y++)
            {
                [unroll]
                for (int x = -1; x <= 1; x++)
                {
                    float2 sampleUv = clamp(
                        (basePosition + float2(x, y) + 0.5) * texel,
                        texel * 0.5,
                        1.0 - texel * 0.5
                    );
                    float sampleDepth = tex2Dlod(
                        _HistoryDepthTex,
                        float4(sampleUv, 0.0, 0.0)
                    ).r;
                    bestDifference = min(
                        bestDifference,
                        RelativeDepthDifference(currentDepth, sampleDepth)
                    );
                }
            }
            return bestDifference;
        }

        float4 SampleHistoryCatmullRom(float2 uv)
        {
            float2 textureSize = _SourceDimensions.xy;
            float2 texelSize = _SourceDimensions.zw;
            float2 position = uv * textureSize - 0.5;
            float2 basePosition = floor(position);
            float2 fraction = position - basePosition;
            float2 fraction2 = fraction * fraction;
            float2 fraction3 = fraction2 * fraction;

            float4 weightsX = float4(
                -0.5 * fraction.x + fraction2.x - 0.5 * fraction3.x,
                1.0 - 2.5 * fraction2.x + 1.5 * fraction3.x,
                0.5 * fraction.x + 2.0 * fraction2.x - 1.5 * fraction3.x,
                -0.5 * fraction2.x + 0.5 * fraction3.x
            );
            float4 weightsY = float4(
                -0.5 * fraction.y + fraction2.y - 0.5 * fraction3.y,
                1.0 - 2.5 * fraction2.y + 1.5 * fraction3.y,
                0.5 * fraction.y + 2.0 * fraction2.y - 1.5 * fraction3.y,
                -0.5 * fraction2.y + 0.5 * fraction3.y
            );

            float4 result = 0.0;
            [unroll]
            for (int y = 0; y < 4; y++)
            {
                [unroll]
                for (int x = 0; x < 4; x++)
                {
                    float2 samplePosition = basePosition + float2(x - 1, y - 1) + 0.5;
                    float2 sampleUv = clamp(
                        samplePosition * texelSize,
                        texelSize * 0.5,
                        1.0 - texelSize * 0.5
                    );
                    result += tex2Dlod(
                        _HistoryTex,
                        float4(sampleUv, 0.0, 0.0)
                    ) * weightsX[x] * weightsY[y];
                }
            }
            return result;
        }

        struct TemporalEvaluation
        {
            float4 current;
            float4 history;
            float4 reprojected;
            float4 clampedHistory;
            float4 resolved;
            float depthRejected;
            float reactive;
            float historyWeight;
            float clampExtent;
            float depthEdge;
        };

        TemporalEvaluation EvaluateTemporal(float2 inputUv)
        {
            TemporalEvaluation evaluation;
            float2 uv = ResolveUv(inputUv);
            float2 currentUv = saturate(uv - _Jitter);
            // Color is de-jittered by sampling the rasterized source at
            // currentUv. Depth and motion must come from the same source-space
            // location; otherwise every Halton sample moves a silhouette across
            // the depth rejection test even when the scene is stationary.
            float rawDepth = SAMPLE_DEPTH_TEXTURE(
                _CameraDepthTexture,
                currentUv
            );
            float currentDepth = Linear01Depth(rawDepth);
            float hasDepth = HasSceneDepth(currentDepth);

            float2 motionUv = currentUv;
            float closestRawDepth = rawDepth;
            float3 allMinimum = 1e20;
            float3 allMaximum = -1e20;
            float allLuminanceMean = 0.0;
            float allLuminanceSquaredMean = 0.0;
            float3 surfaceMinimum = 1e20;
            float3 surfaceMaximum = -1e20;
            float surfaceLuminanceMean = 0.0;
            float surfaceLuminanceSquaredMean = 0.0;
            float surfaceSampleCount = 0.0;
            float localDepthEdge = 0.0;
            [unroll]
            for (int y = -1; y <= 1; y++)
            {
                [unroll]
                for (int x = -1; x <= 1; x++)
                {
                    float2 offset = float2(x, y) * _SourceDimensions.zw;
                    float2 depthUv = saturate(currentUv + offset);
                    float candidateRawDepth = SAMPLE_DEPTH_TEXTURE(
                        _CameraDepthTexture,
                        depthUv
                    );
                    #if defined(UNITY_REVERSED_Z)
                    bool closer = candidateRawDepth > closestRawDepth;
                    #else
                    bool closer = candidateRawDepth < closestRawDepth;
                    #endif
                    if (closer)
                    {
                        closestRawDepth = candidateRawDepth;
                        motionUv = depthUv;
                    }

                    float3 sampleYCoCg = RgbToYCoCg(tex2D(
                        _MainTex,
                        saturate(currentUv + offset)
                    ).rgb);
                    allMinimum = min(allMinimum, sampleYCoCg);
                    allMaximum = max(allMaximum, sampleYCoCg);
                    allLuminanceMean += sampleYCoCg.x;
                    allLuminanceSquaredMean += sampleYCoCg.x * sampleYCoCg.x;

                    float candidateDepth = Linear01Depth(candidateRawDepth);
                    float sameSurface = SameDepthSurface(
                        currentDepth,
                        hasDepth,
                        candidateDepth
                    );
                    localDepthEdge = max(localDepthEdge, 1.0 - sameSurface);
                    if (sameSurface > 0.5)
                    {
                        surfaceMinimum = min(surfaceMinimum, sampleYCoCg);
                        surfaceMaximum = max(surfaceMaximum, sampleYCoCg);
                        surfaceLuminanceMean += sampleYCoCg.x;
                        surfaceLuminanceSquaredMean +=
                            sampleYCoCg.x * sampleYCoCg.x;
                        surfaceSampleCount += 1.0;
                    }
                }
            }

            float edgeBlend = saturate(_DepthEdgeStability * localDepthEdge);
            float2 motion = tex2D(_ReduxBetterAAMotionVectors, motionUv).rg;
            bool invalidMotion = MotionIsInvalid(motion);
            float motionPixels = invalidMotion
                ? 1e20
                : length(motion * _SourceDimensions.xy);

            // Very large launchpad vectors are known to be invalid. A camera-only
            // matrix reprojection is safer than retaining or blindly zeroing them.
            if (invalidMotion || motionPixels > _MaximumMotionPixels)
            {
                float cameraMotionValid;
                float2 cameraMotion = CalculateCameraMotion(
                    uv,
                    rawDepth,
                    cameraMotionValid
                );
                float cameraMotionPixels = length(
                    cameraMotion * _SourceDimensions.xy
                );
                if (cameraMotionValid > 0.5 &&
                    cameraMotionPixels <= _MaximumMotionPixels)
                {
                    motion = cameraMotion;
                    motionPixels = cameraMotionPixels;
                    invalidMotion = false;
                }
                else
                {
                    motion = 0.0;
                }
            }

            // Unity/PPv2 motion encodes current-to-previous UV displacement.
            float2 previousUv = uv - motion;
            float inBounds = step(0.0, previousUv.x) * step(0.0, previousUv.y) *
                             step(previousUv.x, 1.0) * step(previousUv.y, 1.0);
            previousUv = saturate(previousUv);

            evaluation.current = tex2D(_MainTex, currentUv);
            evaluation.history = tex2D(_HistoryTex, uv);
            evaluation.reprojected = SampleHistoryCatmullRom(previousUv);

            allLuminanceMean /= 9.0;
            allLuminanceSquaredMean /= 9.0;
            float inverseSurfaceCount = rcp(max(surfaceSampleCount, 1.0));
            surfaceLuminanceMean *= inverseSurfaceCount;
            surfaceLuminanceSquaredMean *= inverseSurfaceCount;
            float3 neighborhoodMinimum = lerp(
                allMinimum,
                surfaceMinimum,
                edgeBlend
            );
            float3 neighborhoodMaximum = lerp(
                allMaximum,
                surfaceMaximum,
                edgeBlend
            );
            float luminanceMean = lerp(
                allLuminanceMean,
                surfaceLuminanceMean,
                edgeBlend
            );
            float luminanceSquaredMean = lerp(
                allLuminanceSquaredMean,
                surfaceLuminanceSquaredMean,
                edgeBlend
            );
            float sigma = sqrt(max(
                luminanceSquaredMean - luminanceMean * luminanceMean,
                0.0
            ));
            neighborhoodMinimum.x = max(
                neighborhoodMinimum.x,
                luminanceMean - _VarianceGamma * sigma
            );
            neighborhoodMaximum.x = min(
                neighborhoodMaximum.x,
                luminanceMean + _VarianceGamma * sigma
            );

            float3 historyYCoCg = RgbToYCoCg(evaluation.reprojected.rgb);
            float3 clampedYCoCg = clamp(
                historyYCoCg,
                neighborhoodMinimum,
                neighborhoodMaximum
            );
            evaluation.clampedHistory = float4(
                max(YCoCgToRgb(clampedYCoCg), 0.0),
                evaluation.reprojected.a
            );
            evaluation.clampExtent = length(
                neighborhoodMaximum - neighborhoodMinimum
            );

            float historyDepth = tex2D(_HistoryDepthTex, previousUv).r;
            float legacyDepthDifference = RelativeDepthDifference(
                currentDepth,
                historyDepth
            );
            float legacyDepthAccepted = 1.0 - step(
                _DepthThreshold,
                legacyDepthDifference
            );
            legacyDepthAccepted = lerp(1.0, legacyDepthAccepted, hasDepth);
            float depthAccepted = legacyDepthAccepted;
            [branch]
            if (edgeBlend > 0.0001)
            {
                float matchedDepthDifference = BestHistoryDepthDifference(
                    previousUv,
                    currentDepth
                );
                float matchedDepthAccepted = 1.0 - smoothstep(
                    _DepthThreshold,
                    _DepthThreshold * 2.0,
                    matchedDepthDifference
                );
                depthAccepted = lerp(
                    legacyDepthAccepted,
                    matchedDepthAccepted,
                    edgeBlend
                );
            }
            evaluation.depthRejected = 1.0 - depthAccepted;
            evaluation.depthEdge = localDepthEdge;

            float motionFactor = saturate(
                motionPixels / max(_MotionResponsePixels, 0.0001)
            );
            float validMotion = invalidMotion
                ? 0.0
                : 1.0 - step(_MaximumMotionPixels, motionPixels);
            float baseWeight = lerp(
                _StationaryHistory,
                _MovingHistory,
                motionFactor
            );
            baseWeight = lerp(_NoDepthHistory, baseWeight, hasDepth);
            baseWeight = lerp(
                baseWeight,
                max(baseWeight, _StationaryHistory),
                edgeBlend
            );

            float currentLuminance = Luminance(evaluation.current.rgb);
            float historyLuminance = Luminance(lerp(
                evaluation.reprojected.rgb,
                evaluation.clampedHistory.rgb,
                edgeBlend
            ));
            evaluation.reactive = saturate(
                abs(currentLuminance - historyLuminance) * _ReactiveScale
            );
            evaluation.historyWeight = baseWeight * inBounds * validMotion *
                depthAccepted * (1.0 - evaluation.reactive) * _HistoryValid;
            evaluation.resolved = lerp(
                evaluation.current,
                evaluation.clampedHistory,
                evaluation.historyWeight
            );
            evaluation.resolved.rgb = max(evaluation.resolved.rgb, 0.0);
            return evaluation;
        }

        float4 FragResolve(v2f_img input) : SV_Target
        {
            return EvaluateTemporal(input.uv).resolved;
        }

        float4 FragCopyDepth(v2f_img input) : SV_Target
        {
            float2 uv = ResolveUv(input.uv);
            float2 currentUv = saturate(uv - _Jitter);
            float depth = Linear01Depth(
                SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, currentUv)
            );
            return depth.xxxx;
        }

        float4 FragSharpen(v2f_img input) : SV_Target
        {
            float2 uv = ResolveUv(input.uv);
            float2 texel = _SourceDimensions.zw;
            float4 center = tex2D(_MainTex, uv);
            float4 neighbors =
                tex2D(_MainTex, saturate(uv + float2(texel.x, 0.0))) +
                tex2D(_MainTex, saturate(uv - float2(texel.x, 0.0))) +
                tex2D(_MainTex, saturate(uv + float2(0.0, texel.y))) +
                tex2D(_MainTex, saturate(uv - float2(0.0, texel.y)));
            float4 result = center + (center - neighbors * 0.25) * _Sharpening;
            return float4(max(result.rgb, 0.0), center.a);
        }

        float4 FragDebug(v2f_img input) : SV_Target
        {
            TemporalEvaluation evaluation = EvaluateTemporal(input.uv);
            int mode = (int)_DebugMode;
            if (mode == 1) return evaluation.current;
            if (mode == 2) return evaluation.history;
            if (mode == 3) return evaluation.reprojected;
            if (mode == 4) return evaluation.depthRejected.xxxx;
            if (mode == 5) return float4(evaluation.reactive, 0.0, 0.0, 1.0);
            if (mode == 6) return evaluation.historyWeight.xxxx;
            if (mode == 7)
            {
                float extent = evaluation.clampExtent /
                    (1.0 + evaluation.clampExtent);
                return float4(extent, extent * 0.25, 1.0 - extent, 1.0);
            }
            if (mode == 8)
            {
                float2 uv = ResolveUv(input.uv);
                float2 motion = tex2D(_ReduxBetterAAMotionVectors, uv).rg;
                return float4(motion * 0.5 + 0.5, 0.0, 1.0);
            }
            if (mode == 9) return evaluation.depthEdge.xxxx;
            return evaluation.resolved;
        }
        ENDCG

        Pass
        {
            Name "Resolve"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragResolve
            #pragma target 3.0
            ENDCG
        }

        Pass
        {
            Name "CopyLinearDepth"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragCopyDepth
            #pragma target 3.0
            ENDCG
        }

        Pass
        {
            Name "Sharpen"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragSharpen
            #pragma target 3.0
            ENDCG
        }

        Pass
        {
            Name "Debug"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragDebug
            #pragma target 3.0
            ENDCG
        }
    }

    Fallback Off
}
