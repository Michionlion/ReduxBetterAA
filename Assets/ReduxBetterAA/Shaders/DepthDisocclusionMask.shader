Shader "Hidden/ReduxBetterAA/DepthDisocclusionMask"
{
    Properties
    {
        _MainTex ("Scene color", 2D) = "black" {}
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
            sampler2D _MotionTexture;
            float4 _MainTex_TexelSize;
            float4 _SourceDimensions;

            float2 SourceUv(float2 uv)
            {
                #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0.0)
                    uv.y = 1.0 - uv.y;
                #endif
                return saturate(uv);
            }

            float HasSceneDepth(float linearDepth)
            {
                return 1.0 - step(0.99999, linearDepth);
            }

            float RelativeDepthDifference(float a, float b)
            {
                return abs(a - b) / max(max(abs(a), abs(b)), 0.0001);
            }

            float4 Frag(v2f_img input) : SV_Target
            {
                float2 uv = SourceUv(input.uv);
                float rawCenter = SAMPLE_DEPTH_TEXTURE(_DepthTexture, uv);
                float centerDepth = Linear01Depth(rawCenter);
                float centerCovered = HasSceneDepth(centerDepth);
                float edge = 0.0;
                float maximumMotionPixels = 0.0;

                [unroll]
                for (int y = -1; y <= 1; y++)
                {
                    [unroll]
                    for (int x = -1; x <= 1; x++)
                    {
                        float2 sampleUv = saturate(
                            uv + float2(x, y) * _SourceDimensions.zw
                        );
                        float sampleDepth = Linear01Depth(
                            SAMPLE_DEPTH_TEXTURE(_DepthTexture, sampleUv)
                        );
                        float sampleCovered = HasSceneDepth(sampleDepth);
                        float coverageEdge = abs(centerCovered - sampleCovered);
                        float geometryEdge = centerCovered * sampleCovered * step(
                            0.01,
                            RelativeDepthDifference(centerDepth, sampleDepth)
                        );
                        edge = max(edge, max(coverageEdge, geometryEdge));

                        float2 motion = tex2D(_MotionTexture, sampleUv).rg;
                        bool invalid = any(motion != motion) ||
                            any(abs(motion) > 10000.0);
                        float motionPixels = invalid
                            ? 0.0
                            : length(motion * _SourceDimensions.xy);
                        maximumMotionPixels = max(maximumMotionPixels, motionPixels);
                    }
                }

                // Only moving solid silhouettes and solid/solid depth breaks are
                // biased. Broad no-depth/transparent regions stay black so vendor
                // AA can continue accumulating their temporal detail.
                float moving = smoothstep(4.0, 32.0, maximumMotionPixels);
                float mask = saturate(edge * moving);
                return float4(mask, mask, mask, mask);
            }
            ENDCG
        }
    }

    Fallback Off
}
