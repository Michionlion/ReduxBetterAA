Shader "Hidden/ReduxBetterAA/IssueBufferCapture"
{
    Properties { _MainTex ("Source", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "UnityCG.cginc"
        sampler2D _MainTex;
        float _PreviewMode;
        float4 Raw(v2f_img input) : SV_Target
        {
            return tex2D(_MainTex, input.uv);
        }
        float4 Preview(v2f_img input) : SV_Target
        {
            float4 value = tex2D(_MainTex, input.uv);
            if (_PreviewMode == 1) // Device depth. EXR preserves the unmodified sample.
                return float4(pow(saturate(Linear01Depth(value.r)), 0.2).xxx, 1);
            if (_PreviewMode == 2) // Signed UV motion; neutral = grey, scale = 32.
                return float4(saturate(0.5 + value.xy * 32), 0.5, 1);
            if (_PreviewMode == 3) // Single channel masks / linear history depth.
                return float4(saturate(value.rrr), 1);
            if (_PreviewMode == 4)
                return float4(saturate(value.aaa), 1);
            return float4(saturate(value.rgb), 1);
        }
        ENDCG
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment Raw
            #pragma target 3.0
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment Preview
            #pragma target 3.0
            ENDCG
        }
    }
    Fallback Off
}
