Shader "Hidden/DepthBlit"
{
    Properties
    {
        _MainTex ("Base (unused)", 2D) = "white" {} 
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Background" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f    { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            sampler2D _CameraDepthTexture; 
            float4 _ZBufferParams; // x = 1/(far - near), y = -near/(far - near), etc. Provided by Unity.

            float Linear01Depth(float z)
            {
                // Unity’s _CameraDepthTexture holds non-linear depth. Convert to linear 0–1:
                return (1.0 / (_ZBufferParams.x * z + _ZBufferParams.y));
            }

            float4 frag(v2f i) : SV_Target
            {
                // Sample the raw depth
                float rawZ = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                // Convert to linear [0,1]
                float lin = Linear01Depth(rawZ);
                // Write it into the R channel (RFloat RT)
                return float4(lin, 0, 0, 1);
            }
            ENDCG
        }
    }
}
