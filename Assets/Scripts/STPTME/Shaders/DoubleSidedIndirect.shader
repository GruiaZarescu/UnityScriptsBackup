Shader "Custom/DoubleSidedIndirect"
{
    Properties
    {
        _Color("Color", Color) = (1,1,1,1)
        _MainTex("Albedo", 2D) = "white" {}

        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        _Glossiness("Smoothness", Range(0.0, 1.0)) = 0.5
        [Gamma] _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _MetallicGlossMap("Metallic", 2D) = "white" {}

        _BumpScale("Scale", Float) = 1.0
        _BumpMap("Normal Map", 2D) = "bump" {}

        _Parallax ("Height Scale", Range (0.005, 0.08)) = 0.02
        _ParallaxMap ("Height Map", 2D) = "black" {}

        _OcclusionStrength("Strength", Range(0.0, 1.0)) = 1.0
        _OcclusionMap("Occlusion", 2D) = "white" {}

        _EmissionColor("Color", Color) = (0,0,0)
        _EmissionMap("Emission", 2D) = "white" {}

        _DetailMask("Detail Mask", 2D) = "white" {}

        _DetailAlbedoMap("Detail Albedo x2", 2D) = "grey" {}
        _DetailNormalMapScale("Scale", Float) = 1.0
        _DetailNormalMap("Normal Map", 2D) = "bump" {}

        [Enum(UV0,0,UV1,1)] _UVSec ("UV Set for secondary textures", Float) = 0

        // Blending state
        [HideInInspector] _Mode ("__mode", Float) = 0.0
        [HideInInspector] _SrcBlend ("__src", Float) = 1.0
        [HideInInspector] _DstBlend ("__dst", Float) = 0.0
        [HideInInspector] _ZWrite ("__zw", Float) = 1.0

        // Indirect-instancing buffers
        [HideInInspector] _TreeMatrices ("Tree Matrices", Vector) = (0,0,0,0)
        [HideInInspector] _VisibleIndices ("Visible Indices", Vector) = (0,0,0,0)
    }

    CGINCLUDE
        #define UNITY_SETUP_BRDF_INPUT MetallicSetup
    ENDCG

    SubShader
    {
        Tags { "RenderType"="Opaque" "PerformanceChecks"="False" }
        LOD 300

        // ------------------------------------------------------------------
        //  Base forward pass (directional light, emission, lightmaps, ...)
        Pass
        {
            Name "FORWARD"
            Tags { "LightMode" = "ForwardBase" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"
            #include "AutoLight.cginc"
            #include "UnityStandardBRDF.cginc"
            #include "UnityStandardInput.cginc"

            // Indirect-instancing buffers.
            StructuredBuffer<float4x4> _TreeMatrices;
            StructuredBuffer<uint> _VisibleIndices;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float2 uv2 : TEXCOORD3;
                UNITY_FOG_COORDS(4)
                UNITY_SHADOW_COORDS(5)
                float3 worldTangent : TEXCOORD6;
                float3 worldBinormal : TEXCOORD7;
            };

            v2f vert(appdata_full v, uint instanceID : SV_InstanceID)
            {
                v2f o;

                uint matrixIdx = _VisibleIndices[instanceID];
                float4x4 objectToWorld = _TreeMatrices[matrixIdx];

                float4 worldVertex = mul(objectToWorld, v.vertex);
                float4 worldNormal4 = mul(objectToWorld, float4(v.normal, 0.0));

                o.pos = UnityWorldToClipPos(worldVertex);
                o.worldPos = worldVertex.xyz;
                o.worldNormal = normalize(worldNormal4.xyz);
                o.uv = v.texcoord;
                o.uv2 = v.texcoord1;

                // Tangent frame
                float3 worldTangent = mul((float3x3)objectToWorld, v.tangent.xyz);
                o.worldTangent = normalize(worldTangent);
                o.worldBinormal = normalize(cross(o.worldNormal, o.worldTangent) * v.tangent.w);

                UNITY_TRANSFER_FOG(o, o.pos);
                UNITY_TRANSFER_SHADOW(o, v.texcoord1);
                return o;
            }

            // ---- fragment ---- 
            fixed4 frag(v2f i) : SV_Target
            {
                // Standard PBR fragment
                half3 albedo = tex2D(_MainTex, i.uv).rgb * _Color.rgb;
                half3 emission = 0;
                half oneMinusReflectivity;
                half3 specColor;
                albedo = DiffuseAndSpecularFromMetallic(albedo, _Metallic, specColor, oneMinusReflectivity);

                // Normal map
                half3 normalTangent = UnpackScaleNormal(tex2D(_BumpMap, i.uv), _BumpScale);
                half3 normalWorld = normalize(
                    i.worldTangent * normalTangent.x +
                    i.worldBinormal * normalTangent.y +
                    i.worldNormal * normalTangent.z
                );

                half occlusion = tex2D(_OcclusionMap, i.uv).r;
                half smoothness = tex2D(_MetallicGlossMap, i.uv).a * _Glossiness;
                half oneMinusReflectivity2 = oneMinusReflectivity;
                half3 diffColor = albedo;

                // Lighting
                UNITY_LIGHT_ATTENUATION(atten, i, i.worldPos);
                half4 light = half4(
                    _LightColor0.rgb * atten,
                    1
                );

                half3 diffuse = diffColor * light.rgb * max(0, dot(normalWorld, _WorldSpaceLightPos0.xyz));
                half3 specular = specColor * light.rgb * pow(max(0, dot(normalWorld, normalize(_WorldSpaceLightPos0.xyz + normalize(i.worldPos - _WorldSpaceCameraPos)))), smoothness * 200 + 1);

                half4 col = half4(diffuse + specular + emission * albedo, 1);
                UNITY_APPLY_FOG(i.fogCoord, col.rgb);
                return col;
            }
            ENDCG
        }

        // ------------------------------------------------------------------
        //  Forward additive pass
        Pass
        {
            Name "FORWARD_ADDITIVE"
            Tags { "LightMode" = "ForwardAdd" }
            Blend [_SrcBlend] One
            ZWrite Off

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile_fwdadd_fullshadows
            #pragma multi_compile_fog

            #include "UnityCG.cginc"
            #include "AutoLight.cginc"
            #include "UnityStandardBRDF.cginc"
            #include "UnityStandardInput.cginc"

            StructuredBuffer<float4x4> _TreeMatrices;
            StructuredBuffer<uint> _VisibleIndices;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float2 uv : TEXCOORD2;
                UNITY_FOG_COORDS(3)
                UNITY_SHADOW_COORDS(4)
                float3 worldTangent : TEXCOORD5;
                float3 worldBinormal : TEXCOORD6;
            };

            v2f vert(appdata_full v, uint instanceID : SV_InstanceID)
            {
                v2f o;

                uint matrixIdx = _VisibleIndices[instanceID];
                float4x4 objectToWorld = _TreeMatrices[matrixIdx];

                float4 worldVertex = mul(objectToWorld, v.vertex);
                float4 worldNormal4 = mul(objectToWorld, float4(v.normal, 0.0));

                o.pos = UnityWorldToClipPos(worldVertex);
                o.worldPos = worldVertex.xyz;
                o.worldNormal = normalize(worldNormal4.xyz);
                o.uv = v.texcoord;

                float3 worldTangent = mul((float3x3)objectToWorld, v.tangent.xyz);
                o.worldTangent = normalize(worldTangent);
                o.worldBinormal = normalize(cross(o.worldNormal, o.worldTangent) * v.tangent.w);

                UNITY_TRANSFER_FOG(o, o.pos);
                UNITY_TRANSFER_SHADOW(o, v.texcoord1);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                half3 albedo = tex2D(_MainTex, i.uv).rgb * _Color.rgb;
                half3 emission = 0;
                half oneMinusReflectivity;
                half3 specColor;
                albedo = DiffuseAndSpecularFromMetallic(albedo, _Metallic, specColor, oneMinusReflectivity);

                half3 normalTangent = UnpackScaleNormal(tex2D(_BumpMap, i.uv), _BumpScale);
                half3 normalWorld = normalize(
                    i.worldTangent * normalTangent.x +
                    i.worldBinormal * normalTangent.y +
                    i.worldNormal * normalTangent.z
                );

                half smoothness = tex2D(_MetallicGlossMap, i.uv).a * _Glossiness;

                UNITY_LIGHT_ATTENUATION(atten, i, i.worldPos);
                half3 lightDir = normalize(UnityWorldSpaceLightDir(i.worldPos));
                half3 diffuse = albedo * _LightColor0.rgb * atten * max(0, dot(normalWorld, lightDir));
                half3 specular = specColor * _LightColor0.rgb * atten * pow(max(0, dot(normalWorld, normalize(lightDir + normalize(i.worldPos - _WorldSpaceCameraPos)))), smoothness * 200 + 1);

                half4 col = half4(diffuse + specular, 1);
                UNITY_APPLY_FOG(i.fogCoord, col.rgb);
                return col;
            }
            ENDCG
        }

        // ------------------------------------------------------------------
        //  Shadow caster pass
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"
            #include "UnityStandardInput.cginc"

            StructuredBuffer<float4x4> _TreeMatrices;
            StructuredBuffer<uint> _VisibleIndices;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
            };

            v2f vert(appdata_full v, uint instanceID : SV_InstanceID)
            {
                v2f o;

                uint matrixIdx = _VisibleIndices[instanceID];
                float4x4 objectToWorld = _TreeMatrices[matrixIdx];

                float4 worldVertex = mul(objectToWorld, v.vertex);
                o.worldPos = worldVertex.xyz;
                o.pos = UnityWorldToClipPos(worldVertex);
                o.uv = v.texcoord;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Alpha test
                half alpha = tex2D(_MainTex, i.uv).a;
                clip(alpha - _Cutoff);

                // Depth is written automatically from SV_POSITION.z (SM4.5).
                // Color output is unused for shadow maps.
                return 0;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}