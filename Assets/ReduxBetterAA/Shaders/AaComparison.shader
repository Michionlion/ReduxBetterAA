Shader "Hidden/ReduxBetterAA/AaComparison"
{
    Properties { _MainTex ("Left", 2D) = "black" {} _RightTex ("Right", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _RightTex;
            float _Split, _OutputWidth;
            float4 frag(v2f_img i) : SV_Target
            {
                // One white output pixel with a black pixel on either side.
                // Use display dimensions, independent of either SSAA target.
                float distancePixels = abs(floor(i.uv.x * _OutputWidth) - floor(_Split * _OutputWidth));
                if (_Split > 0 && _Split < 1 && distancePixels <= 1)
                    return distancePixels < 0.5 ? float4(1, 1, 1, 1) : float4(0, 0, 0, 1);
                // Both arms are full-frame renders with identical RT orientation.
                // Crop each half at its original UV; never squeeze two views.
                return i.uv.x < _Split ? tex2D(_MainTex, i.uv) : tex2D(_RightTex, i.uv);
            }
            ENDCG
        }
    }
}
