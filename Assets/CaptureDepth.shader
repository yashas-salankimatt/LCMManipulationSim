// This shader captures depth from the camera's depth texture
// Place this in a new shader file called "CaptureDepth.shader"
Shader "Hidden/CaptureDepth"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _DepthMultiplier ("Depth Multiplier", Float) = 1.0
        _InvertDepth ("Invert Depth", Float) = 0.0
    }
    SubShader
    {
        // No culling or depth
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            #include "UnityCG.cginc"
            
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            sampler2D _CameraDepthTexture;
            float4 _MainTex_ST;
            float _DepthMultiplier;
            float _InvertDepth;
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }
            
            fixed4 frag (v2f i) : SV_Target
            {
                // Sample the depth texture
                float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                
                // Convert to linear depth
                float linearDepth = Linear01Depth(depth);
                
                // Apply multiplier
                linearDepth *= _DepthMultiplier;
                
                // Invert if needed
                if (_InvertDepth > 0.5)
                    linearDepth = 1.0 - linearDepth;
                
                // Clamp to valid range
                linearDepth = saturate(linearDepth);
                
                return fixed4(linearDepth, linearDepth, linearDepth, 1.0);
            }
            ENDCG
        }
    }
}