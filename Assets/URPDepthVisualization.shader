Shader "Custom/URPDepthVisualization"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _DepthScale ("Depth Scale", Float) = 1.0
        _InvertDepth ("Invert Depth", Float) = 1.0
    }
    
    SubShader
    {
        Tags 
        { 
            "RenderType"="Opaque" 
            "RenderPipeline" = "UniversalPipeline"
        }
        
        Pass
        {
            Name "DepthVisualization"
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            
            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };
            
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            
            float _DepthScale;
            float _InvertDepth;
            
            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }
            
            half4 frag(Varyings input) : SV_Target
            {
                // Sample depth from URP's depth texture
                #if UNITY_REVERSED_Z
                    float rawDepth = SampleSceneDepth(input.uv);
                #else
                    float rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1, SampleSceneDepth(input.uv));
                #endif
                
                // Convert to linear 0-1 depth
                float linearDepth = Linear01Depth(rawDepth, _ZBufferParams);
                
                // Apply scale
                linearDepth *= _DepthScale;
                
                // Invert if needed
                if (_InvertDepth > 0.5)
                    linearDepth = 1.0 - linearDepth;
                
                // Clamp to valid range
                linearDepth = saturate(linearDepth);
                
                return half4(linearDepth, linearDepth, linearDepth, 1.0);
            }
            ENDHLSL
        }
    }
    
    // Fallback for older Unity versions
    SubShader
    {
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

            sampler2D _CameraDepthTexture;
            float _DepthScale;
            float _InvertDepth;
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            
            fixed4 frag (v2f i) : SV_Target
            {
                float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                float linearDepth = Linear01Depth(depth);
                
                linearDepth *= _DepthScale;
                
                if (_InvertDepth > 0.5)
                    linearDepth = 1.0 - linearDepth;
                    
                return fixed4(linearDepth, linearDepth, linearDepth, 1.0);
            }
            ENDCG
        }
    }
}