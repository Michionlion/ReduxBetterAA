Shader "Hidden/ReduxBetterAA/FsrBridgeInputs"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragDepth
            #include "UnityCG.cginc"
            UNITY_DECLARE_DEPTH_TEXTURE(_MainTex);
            float4 _MainTex_TexelSize;
            float FragDepth(v2f_img input) : SV_Target
            {
                float2 uv = input.uv;
                #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0.0) uv.y = 1.0 - uv.y;
                #endif
                return SAMPLE_DEPTH_TEXTURE(_MainTex, uv);
            }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragExposure
            #include "UnityCG.cginc"
            // Input is post-PPv2 linear LDR, with no scene pre-exposure.
            float FragExposure(v2f_img input) : SV_Target { return 1.0; }
            ENDCG
        }
    }
}
