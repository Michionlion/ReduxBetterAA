Shader "Hidden/ReduxBetterAA/FrameGenerationInputs"
{
    Properties
    {
        _MainTex ("Current source", 2D) = "white" {}
        _EncodeSrgb ("Encode linear color", Float) = 0
        _NativeFlipY ("Native texture row orientation", Float) = 0
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        // Convert the already post-processed AA result into encoded SDR storage.
        // The native final swapchain is RGBA8_UNORM with encoded SDR values.
        // This shader is not a tonemapper; HDR output is rejected by the host.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment ColorInput
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _EncodeSrgb;
            float _NativeFlipY;
            float4 ColorInput(v2f_img input) : SV_Target
            {
                float2 uv=input.uv;
                if (_NativeFlipY > 0.5) uv.y=1-uv.y;
                float3 c = saturate(tex2D(_MainTex, uv).rgb);
                if (_EncodeSrgb > 0.5)
                    c = lerp(12.92 * c, 1.055 * pow(c, 1.0 / 2.4) - 0.055, step(0.0031308, c));
                return float4(c, 1);
            }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment DepthInput
            #include "UnityCG.cginc"
            UNITY_DECLARE_DEPTH_TEXTURE(_MainTex);
            float4 _MainTex_TexelSize;
            float _NativeFlipY;
            float DepthInput(v2f_img input) : SV_Target
            {
                float2 uv = input.uv;
                #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0) uv.y = 1 - uv.y;
                #endif
                if (_NativeFlipY > 0.5) uv.y=1-uv.y;
                return SAMPLE_DEPTH_TEXTURE(_MainTex, uv);
            }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment MotionInput
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _NativeFlipY;
            float2 MotionInput(v2f_img input) : SV_Target
            {
                // The FG-owned snapshot sanitizer already applied rejection and
                // restored the stored component signs. Do not apply signs twice.
                float2 uv=input.uv;
                if (_NativeFlipY > 0.5) uv.y=1-uv.y;
                // Spatial orientation is separate from vector direction. The
                // native camera's explicit scale converts bottom-left vectors.
                return tex2D(_MainTex, uv).rg;
            }
            ENDCG
        }
    }
}
