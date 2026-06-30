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
                float3 _Padding;
                float3 _CameraPos;
            CBUFFER_END

            struct InstanceData
            {
                float3 worldPos;
                float heightScale;
                uint packedMeta;
                uint seed;
                float widthScale;
                uint pad3;
            };
            StructuredBuffer<InstanceData> _InstanceOutputBuffer;
            
            // Prototype scales buffer
            StructuredBuffer<float3> _PrototypeScales;
            StructuredBuffer<uint> _ProtoMaxLODs; 

            // Sphere constants (needed for orientation)
            float3 _SphereCenter;

            Varyings vert(Attributes IN, uint instanceID : SV_InstanceID)
            {
                Varyings OUT;
                
                uint actualInstanceID = instanceID + (uint)_InstanceOffset;
                InstanceData inst = _InstanceOutputBuffer[actualInstanceID];

                uint protoIdx = inst.packedMeta & 0xFF;
                uint chunkLOD = (inst.packedMeta >> 8) & 0xFF;
                uint rotQ = (inst.packedMeta >> 16) & 0xFF;
                // scaleQ is no longer used - we use heightScale/widthScale directly

                float rotation = rotQ / 255.0 * 6.283185;
                
                float3 baseScale = _PrototypeScales[protoIdx];
                // Apply height and width scales separately
                float3 finalScale = float3(
                    baseScale.x * inst.widthScale,
                    baseScale.y * inst.heightScale,
                    baseScale.z * inst.widthScale
                );

                // 1. Orient the tree to the sphere surface
                float3 dir = normalize(inst.worldPos - _SphereCenter);
                float3 localUp = abs(dir.y) < 0.99 ? float3(0,1,0) : float3(1,0,0);
                float3 binormal = normalize(cross(localUp, dir));
                float3 tangent = cross(dir, binormal);

                // 2. Apply scale and Y-axis rotation in local space (for 3D meshes)
                float s, c; sincos(rotation, s, c);
                float3 localPos = IN.positionOS.xyz * finalScale;
                float3 rotatedPos;
                rotatedPos.x = localPos.x * c - localPos.z * s;
                rotatedPos.z = localPos.x * s + localPos.z * c;
                rotatedPos.y = localPos.y;

                float3 rotatedNormal;
                rotatedNormal.x = IN.normalOS.x * c - IN.normalOS.z * s;
                rotatedNormal.z = IN.normalOS.x * s + IN.normalOS.z * c;
                rotatedNormal.y = IN.normalOS.y;

                float3 worldOffset;
                float3 finalNormal;

                // 3. CHECK IF BILLBOARD (Max LOD for this specific prototype)
                uint maxLod = _ProtoMaxLODs[protoIdx];
                if (chunkLOD >= maxLod) 
                {
                    // BILLBOARD PATH:
                    // Use localPos directly, ignoring per-instance Y-rotation!
                    // We want a cylindrical billboard: up is the sphere normal, forward faces camera.
                    float3 up = dir;
                    float3 forward = normalize(_CameraPos - inst.worldPos);
                    // Project forward onto the plane perpendicular to up
                    float3 forwardProj = normalize(forward - up * dot(forward, up));
                    float3 right = cross(up, forwardProj);
                    
                    // Map the mesh vertices to the camera-facing basis
                    worldOffset = right * localPos.x + up * localPos.y + forwardProj * localPos.z;
                    
                    // Normal faces the camera
                    finalNormal = forwardProj;
                }
                else
                {
                    // NORMAL 3D MESH PATH:
                    worldOffset = tangent * rotatedPos.x + dir * rotatedPos.y + binormal * rotatedPos.z;
                    finalNormal = normalize(tangent * rotatedNormal.x + dir * rotatedNormal.y + binormal * rotatedNormal.z);
                }

                float3 worldPos = inst.worldPos + worldOffset;

                OUT.positionHCS = TransformWorldToHClip(worldPos);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.worldNormal = finalNormal;

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 texColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                
                float3 normalWS = normalize(IN.worldNormal);
                Light mainLight = GetMainLight();
                float NdotL = saturate(dot(normalWS, mainLight.direction));
                float3 ambient = SampleSH(normalWS);
                
                half3 finalColor = texColor.rgb * _Color.rgb * (mainLight.color * NdotL + ambient);
                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }
}