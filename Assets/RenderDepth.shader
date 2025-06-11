Shader "Custom/RenderDepth"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            #include "UnityCG.cginc"
            
            struct appdata
            {
                float4 vertex : POSITION;
            };
            
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float depth : TEXCOORD0;
            };
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                
                // Calculate depth in clip space (0 to 1)
                o.depth = o.vertex.z / o.vertex.w;
                
                // For more precision, you can use:
                // o.depth = -UnityObjectToViewPos(v.vertex).z;
                
                return o;
            }
            
            float4 frag (v2f i) : SV_Target
            {
                // Output depth as grayscale
                // The depth value is already in 0-1 range from vertex shader
                float depth = i.depth;
                
                // Option: Apply non-linear transformation for better visualization
                // depth = pow(depth, 0.5); // Square root for better near-plane detail
                
                return float4(depth, depth, depth, 1.0);
            }
            ENDCG
        }
    }
    
    // Fallback for transparent objects
    SubShader
    {
        Tags { "RenderType"="Transparent" }
        
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            #include "UnityCG.cginc"
            
            struct appdata
            {
                float4 vertex : POSITION;
            };
            
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float depth : TEXCOORD0;
            };
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.depth = o.vertex.z / o.vertex.w;
                return o;
            }
            
            float4 frag (v2f i) : SV_Target
            {
                float depth = i.depth;
                return float4(depth, depth, depth, 1.0);
            }
            ENDCG
        }
    }
    
    // Fallback for other render types
    Fallback "VertexLit"
}