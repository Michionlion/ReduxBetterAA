Shader "Hidden/ReduxBetterAA/Phase1MotionStatistics"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "MotionStatistics"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment Frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _CameraDepthTexture;
            sampler2D _CameraMotionVectorsTexture;
            float4 _MainTex_TexelSize;

            float2 DiagnosticUv(float2 uv)
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

            float4 Frag(v2f_img input) : SV_Target
            {
                float2 uv = DiagnosticUv(input.uv);
                float2 motion = tex2D(_CameraMotionVectorsTexture, uv).rg;
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                float linearDepth = Linear01Depth(rawDepth);
                return MotionIsInvalid(motion)
                    ? float4(0.0, 0.0, linearDepth, 0.0)
                    : float4(motion, linearDepth, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
