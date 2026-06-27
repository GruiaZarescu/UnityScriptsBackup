Shader "Custom/ImpostorInstanced_URP"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "DisableBatching"="True" }
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float3 normalOS     : NORMAL;
                float2 uv           : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS  : SV_POSITION;
                float2 uv           : TEXCOORD0;
                float3 worldNormal  : TEXCOORD1;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
                float _InstanceOffset;
                float3 _Padding; // Pad to 16 bytes so URP doesn't strip it!
            CBUFFER_END

            struct InstanceData
            {
                float3 worldPos;
                float pad1;
                uint packedMeta;
                uint seed;
                uint pad2;
                uint pad3;
            };
            StructuredBuffer<InstanceData> _InstanceOutputBuffer;

            Varyings vert(Attributes IN, uint instanceID : SV_InstanceID)
            {
                Varyings OUT;
                
                uint actualInstanceID = instanceID + (uint)_InstanceOffset;
                InstanceData inst = _InstanceOutputBuffer[actualInstanceID];

                uint chunkLOD = (inst.packedMeta >> 8) & 0xFF;
                uint rotQ = (inst.packedMeta >> 16) & 0xFF;
                uint scaleQ = (inst.packedMeta >> 24) & 0xFF;

                float rotation = rotQ / 255.0 * 6.283185;
                float scale = lerp(0.5, 2.0, scaleQ / 255.0);

                // Build rotation matrix around Y (local up on sphere = radial)
                float s, c; sincos(rotation, s, c);
                float3 localPos = IN.positionOS.xyz;
                localPos.xz *= scale;
                float3 rotatedPos;
                rotatedPos.x = localPos.x * c - localPos.z * s;
                rotatedPos.z = localPos.x * s + localPos.z * c;
                rotatedPos.y = localPos.y * scale;
                
                float3 worldPos = inst.worldPos + rotatedPos;

                OUT.positionHCS = TransformWorldToHClip(worldPos);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                
                // Rotate the normal exactly the same way as the position
                float3 rotatedNormal;
                rotatedNormal.x = IN.normalOS.x * c - IN.normalOS.z * s;
                rotatedNormal.z = IN.normalOS.x * s + IN.normalOS.z * c;
                rotatedNormal.y = IN.normalOS.y;
                OUT.worldNormal = TransformObjectToWorldDir(rotatedNormal);

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 texColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                
                // URP Lighting
                float3 normalWS = normalize(IN.worldNormal);
                Light mainLight = GetMainLight();
                
                // Diffuse lighting
                float NdotL = saturate(dot(normalWS, mainLight.direction));
                
                // Ambient lighting (environment probes)
                float3 ambient = SampleSH(normalWS);
                
                half3 finalColor = texColor.rgb * _Color.rgb * (mainLight.color * NdotL + ambient);
                
                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }
}