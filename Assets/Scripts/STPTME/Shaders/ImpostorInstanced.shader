Shader "Custom/ImpostorInstanced_URP"
{
    Properties
    {
        _Color ("Color Tint", Color) = (1,1,1,1)
        _MainTex ("Texture", 2D) = "white" {}

        [Header(Grass Colors)]
        _Top_Color ("Top Color", Color) = (0.145, 0.454, 0.129, 1)
        _Bottom_Color ("Bottom Color", Color) = (0.137, 0.498, 0.314, 1)
        _Gradient_Strength ("Gradient Strength", Range(0, 10)) = 2

        [Header(Color Variation)]
        [NoScaleOffset] _Variation_Texture ("Variation Texture", 2D) = "white" {}
        _Variation_Offset ("Variation Offset", Vector) = (1, 1, 0, 0)
        _Color_Variation_Scale ("Color Variation Scale", Float) = 0.01
        _Color_Variation ("Color Variation", Color) = (0.539, 0.575, 0.236, 1)

        [Header(Grass Color Toggle)]
        [Toggle(_USEGRASSCOLOR_ON)] _UseGrassColor ("Use Grass Color Effects", Float) = 0

        [Header(Wind)]
        [NoScaleOffset] _Wind_Line ("Wind Line Texture", 2D) = "white" {}
        _Wind_Color ("Wind Color", Color) = (1, 1, 1, 0)
        _Offset ("Wind Bias (Offset)", Range(0, 1)) = 0
        _Wind_Direction ("Wind Direction (XZ)", Vector) = (20, 0, 0, 0)
        _Wind_Speed ("Wind Speed", Float) = 1
        _Wind_Strength ("Wind Strength", Float) = 1
        _Wind_Line_Scale ("Wind Line Scale", Float) = 0.02
        _Wind_Gradient ("Wind Gradient (height mask)", Range(0, 1)) = 0.2
    }

    SubShader
    {
        // AlphaTest queue so alpha clip works correctly with shadows and depth prepass
        Tags
        {
            "RenderType"="TransparentCutout"
            "Queue"="AlphaTest"
            "RenderPipeline"="UniversalPipeline"
            "DisableBatching"="True"
        }
        LOD 100

        // ─────────────────────────────────────────────────────────────────────
        // PASS 1 — Forward Lit
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off          // Double-sided — grass cards need both faces
            AlphaToMask On    // MSAA-friendly alpha clip

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5
            #pragma shader_feature _USEGRASSCOLOR_ON
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // ── Structs ──────────────────────────────────────────────────────

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                nointerpolation uint protoIdx : TEXCOORD2;
                nointerpolation uint chunkLOD : TEXCOORD5;
                float  windMask    : TEXCOORD3; // pre-baked wind intensity → fragment color tint
                float2 worldPosXZ  : TEXCOORD4; // world XZ for variation texture UV
            };

            // ── Textures ─────────────────────────────────────────────────────

            TEXTURE2D(_MainTex);           SAMPLER(sampler_MainTex);
            TEXTURE2D(_Wind_Line);         SAMPLER(sampler_Wind_Line);
            TEXTURE2D(_Variation_Texture); SAMPLER(sampler_Variation_Texture);

            // ── Constant buffer ───────────────────────────────────────────────
            // Keep every variable float4-row-friendly to avoid cross-boundary issues.
            // float4  = 16 b / float2+float+float = 16 b / float3+float = 16 b

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;

                float4 _Color;
                float4 _Top_Color;
                float4 _Bottom_Color;
                float4 _Color_Variation;
                float4 _Wind_Color;

                // row: wind dir (xz), speed, strength
                float2 _Wind_Direction;
                float  _Wind_Speed;
                float  _Wind_Strength;

                // row: wind scale, wind gradient, offset, gradient strength
                float  _Wind_Line_Scale;
                float  _Wind_Gradient;
                float  _Offset;
                float  _Gradient_Strength;

                // row: variation offset, scale, instance offset
                float2 _Variation_Offset;
                float  _Color_Variation_Scale;
                float  _InstanceOffset;

                // row: camera pos + pad
                float3 _CameraPos;
                float  _CBPad;
            CBUFFER_END

            // ── Instance buffer & per-proto buffers ───────────────────────────

            struct InstanceData
            {
                float3 worldPos;
                float  heightScale;
                uint   packedMeta;
                uint   seed;
                float  widthScale;
                uint   pad3;
            };
            StructuredBuffer<InstanceData> _InstanceOutputBuffer;
            StructuredBuffer<float3>       _PrototypeScales;
            StructuredBuffer<uint>         _ProtoMaxLODs;
            StructuredBuffer<float4>       _ProtoColorParamsBuffer;

            // Set as a global (Shader.SetGlobalVector) — not a material property
            float3 _SphereCenter;

            // ── Vertex ────────────────────────────────────────────────────────

            Varyings vert(Attributes IN, uint instanceID : SV_InstanceID)
            {
                Varyings OUT;

                uint actualID = instanceID + (uint)_InstanceOffset;
                InstanceData inst = _InstanceOutputBuffer[actualID];

                uint protoIdx = inst.packedMeta & 0xFF;
                uint chunkLOD = (inst.packedMeta >> 8)  & 0xFF;
                OUT.chunkLOD = chunkLOD;
                uint rotQ     = (inst.packedMeta >> 16) & 0xFF;

                float rotation = (rotQ / 255.0) * 6.283185;

                float3 baseScale  = _PrototypeScales[protoIdx];
                float3 finalScale = float3(
                    baseScale.x * inst.widthScale,
                    baseScale.y * inst.heightScale,
                    baseScale.z * inst.widthScale
                );

                // ── Surface tangent frame (sphere orientation) ──
                float3 dir      = normalize(inst.worldPos - _SphereCenter);
                float3 localUp  = abs(dir.y) < 0.99 ? float3(0,1,0) : float3(1,0,0);
                float3 binormal = normalize(cross(localUp, dir));
                float3 tangent  = cross(dir, binormal);

                // ── Scale + Y-axis rotation in local space ──
                float s, c;
                sincos(rotation, s, c);
                float3 localPos = IN.positionOS.xyz * finalScale;
                float3 rotatedPos;
                rotatedPos.x = localPos.x * c - localPos.z * s;
                rotatedPos.y = localPos.y;
                rotatedPos.z = localPos.x * s + localPos.z * c;

                float3 rotatedNormal;
                rotatedNormal.x = IN.normalOS.x * c - IN.normalOS.z * s;
                rotatedNormal.y = IN.normalOS.y;
                rotatedNormal.z = IN.normalOS.x * s + IN.normalOS.z * c;

                // ── Billboard vs. 3-D mesh ──
                float3 worldOffset;
                float3 finalNormal;
                uint   maxLod = _ProtoMaxLODs[protoIdx];

                if (chunkLOD >= maxLod)
                {
                    // Cylindrical billboard — forward faces camera, up = sphere normal
                    float3 forward     = normalize(_CameraPos - inst.worldPos);
                    float3 forwardProj = normalize(forward - dir * dot(forward, dir));
                    float3 right       = cross(dir, forwardProj);

                    worldOffset = right * localPos.x + dir * localPos.y + forwardProj * localPos.z;
                    finalNormal = forwardProj;
                }
                else
                {
                    worldOffset = tangent  * rotatedPos.x
                                + dir      * rotatedPos.y
                                + binormal * rotatedPos.z;
                    finalNormal = normalize(
                                    tangent  * rotatedNormal.x +
                                    dir      * rotatedNormal.y +
                                    binormal * rotatedNormal.z);
                }

                float3 worldPos = inst.worldPos + worldOffset;

                // ── Wind displacement (sphere-aware) ──────────────────────────
                // The sphere normal at the instance's base tells us which direction
                // is "outward" at this point. Any wind component along that direction
                // would push vertices radially — into or out of the sphere — which
                // is what causes floating tips and the fractured/sheared look.
                // We remove that component before applying the displacement.
                float3 sphereNormal = normalize(inst.worldPos - _SphereCenter);

                // Sample wind texture — UV uses world XZ so the pattern tiles
                // consistently across the globe regardless of sphere orientation.
                float2 windDir2 = normalize(_Wind_Direction);
                float2 windUV   = (worldPos.xz + windDir2 * (_Wind_Speed * _Time.y))
                                  * _Wind_Line_Scale;
                float4 windSample = SAMPLE_TEXTURE2D_LOD(_Wind_Line, sampler_Wind_Line,
                                                          windUV, 0);

                // Bias and scale (mirrors asset shader: sample − _Offset × strength)
                float3 rawWind = (windSample.xyz - _Offset) * _Wind_Strength;

                // Project onto the tangent plane: subtract the radial component.
                // dot(rawWind, sphereNormal) gives how much of the wind points
                // outward; subtracting that leaves only tangent (surface-parallel) motion.
                float3 tangentWind = rawWind - dot(rawWind, sphereNormal) * sphereNormal;

                // Height gradient mask: base of blade (uv.y = 0) stays planted,
                // tip (uv.y = 1) gets full displacement.
                float heightMask = saturate(IN.uv.y * _Wind_Gradient);

                // Store intensity for fragment wind-color tint
                OUT.windMask = heightMask * saturate(length(windSample.xyz - _Offset));

                worldPos += tangentWind * heightMask;
                // ─────────────────────────────────────────────────────────────

                OUT.positionHCS = TransformWorldToHClip(worldPos);
                OUT.uv          = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.worldNormal = finalNormal;
                OUT.protoIdx    = protoIdx;
                OUT.worldPosXZ  = worldPos.xz;

                return OUT;
            }

            // ── Fragment ──────────────────────────────────────────────────────

            half4 frag(Varyings IN, bool isFront : SV_IsFrontFace) : SV_Target
            {
                half4 texColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                clip(texColor.a - 0.5);

                // LOD>=1 is single-sided: kill back faces (LOD0 stays double-sided up close).
                if (IN.chunkLOD >= 1 && !isFront) discard;

                // ── Color tint ────────────────────────────────────────────────
                float4 colorOverride = _ProtoColorParamsBuffer[IN.protoIdx];
                float3 tint = colorOverride.rgb * _Color.rgb;

                #if defined(_USEGRASSCOLOR_ON)
                    // Grass path: procedural gradient + world variation + wind tint.
                    // _MainTex is typically white for these materials —
                    // the gradient IS the color, texColor just modulates it.

                    // Vertical gradient: uv.y = 0 at base, 1 at tip
                    float  gradient   = saturate(IN.uv.y * _Gradient_Strength);
                    float3 grassColor = lerp(_Top_Color.rgb, _Bottom_Color.rgb, gradient);

                    // World-space variation texture breaks up uniform tiling
                    float2 varUV     = IN.worldPosXZ * _Color_Variation_Scale + _Variation_Offset;
                    float4 varSample = SAMPLE_TEXTURE2D(_Variation_Texture, sampler_Variation_Texture, varUV);
                    grassColor       = lerp(grassColor, _Color_Variation.rgb, varSample.r);

                    // Wind color streak — bright highlight where wind is strongest
                    grassColor = lerp(grassColor, _Wind_Color.rgb, saturate(IN.windMask));

                    tint *= grassColor;
                #endif
                // Tree / textured-mesh path: tint is just colorOverride × _Color,
                // texColor carries all the visual detail.

                // ── Lighting ──────────────────────────────────────────────────
                float3 normalWS  = normalize(IN.worldNormal);
                Light  mainLight = GetMainLight();
                float  NdotL     = saturate(dot(normalWS, mainLight.direction));
                float3 ambient   = SampleSH(normalWS);

                float3 finalColor = texColor.rgb * tint * (mainLight.color * NdotL + ambient);
                return half4(finalColor, texColor.a);
            }
            ENDHLSL
        }

        // ─────────────────────────────────────────────────────────────────────
        // PASS 2 — Shadow Caster
        // Same vertex logic as ForwardLit (including wind so shadow silhouette
        // matches the visual). Fragment only does alpha clip.
        // ─────────────────────────────────────────────────────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex   vertShadow
            #pragma fragment fragShadow
            #pragma target   4.5
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // URP sets these as globals when rendering shadow maps.
            // They are NOT in Shadows.hlsl — they live in ShadowCasterPass.hlsl,
            // which we cannot include because it defines its own vert/frag.
            float3 _LightDirection;
            float3 _LightPosition;  // only used for punctual (spot/point) shadows

            struct AttributesShadow
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct VaryingsShadow
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
            };

            TEXTURE2D(_MainTex);   SAMPLER(sampler_MainTex);
            TEXTURE2D(_Wind_Line); SAMPLER(sampler_Wind_Line);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;

                float4 _Color;
                float4 _Top_Color;
                float4 _Bottom_Color;
                float4 _Color_Variation;
                float4 _Wind_Color;

                float2 _Wind_Direction;
                float  _Wind_Speed;
                float  _Wind_Strength;

                float  _Wind_Line_Scale;
                float  _Wind_Gradient;
                float  _Offset;
                float  _Gradient_Strength;

                float2 _Variation_Offset;
                float  _Color_Variation_Scale;
                float  _InstanceOffset;

                float3 _CameraPos;
                float  _CBPad;
            CBUFFER_END

            struct InstanceData
            {
                float3 worldPos;
                float  heightScale;
                uint   packedMeta;
                uint   seed;
                float  widthScale;
                uint   pad3;
            };
            StructuredBuffer<InstanceData> _InstanceOutputBuffer;
            StructuredBuffer<float3>       _PrototypeScales;
            StructuredBuffer<uint>         _ProtoMaxLODs;

            float3 _SphereCenter;

            VaryingsShadow vertShadow(AttributesShadow IN, uint instanceID : SV_InstanceID)
            {
                VaryingsShadow OUT;

                uint actualID = instanceID + (uint)_InstanceOffset;
                InstanceData inst = _InstanceOutputBuffer[actualID];

                uint protoIdx = inst.packedMeta & 0xFF;
                uint chunkLOD = (inst.packedMeta >> 8)  & 0xFF;
                uint rotQ     = (inst.packedMeta >> 16) & 0xFF;

                float rotation = (rotQ / 255.0) * 6.283185;

                float3 baseScale  = _PrototypeScales[protoIdx];
                float3 finalScale = float3(
                    baseScale.x * inst.widthScale,
                    baseScale.y * inst.heightScale,
                    baseScale.z * inst.widthScale
                );

                float3 dir      = normalize(inst.worldPos - _SphereCenter);
                float3 localUp  = abs(dir.y) < 0.99 ? float3(0,1,0) : float3(1,0,0);
                float3 binormal = normalize(cross(localUp, dir));
                float3 tangent  = cross(dir, binormal);

                float s, c;
                sincos(rotation, s, c);
                float3 localPos = IN.positionOS.xyz * finalScale;
                float3 rotatedPos;
                rotatedPos.x = localPos.x * c - localPos.z * s;
                rotatedPos.y = localPos.y;
                rotatedPos.z = localPos.x * s + localPos.z * c;

                float3 rotatedNormal;
                rotatedNormal.x = IN.normalOS.x * c - IN.normalOS.z * s;
                rotatedNormal.y = IN.normalOS.y;
                rotatedNormal.z = IN.normalOS.x * s + IN.normalOS.z * c;

                float3 worldOffset;
                float3 finalNormal;
                uint   maxLod = _ProtoMaxLODs[protoIdx];

                if (chunkLOD >= maxLod)
                {
                    float3 forward     = normalize(_CameraPos - inst.worldPos);
                    float3 forwardProj = normalize(forward - dir * dot(forward, dir));
                    float3 right       = cross(dir, forwardProj);
                    worldOffset = right * localPos.x + dir * localPos.y + forwardProj * localPos.z;
                    finalNormal = forwardProj;
                }
                else
                {
                    worldOffset = tangent  * rotatedPos.x
                                + dir      * rotatedPos.y
                                + binormal * rotatedPos.z;
                    finalNormal = normalize(
                                    tangent  * rotatedNormal.x +
                                    dir      * rotatedNormal.y +
                                    binormal * rotatedNormal.z);
                }

                float3 worldPos = inst.worldPos + worldOffset;

                // Wind — identical to forward pass so shadow silhouette matches
                float3 sphereNormal = normalize(inst.worldPos - _SphereCenter);
                float2 windDir2 = normalize(_Wind_Direction);
                float2 windUV   = (worldPos.xz + windDir2 * (_Wind_Speed * _Time.y))
                                  * _Wind_Line_Scale;
                float4 windSample = SAMPLE_TEXTURE2D_LOD(_Wind_Line, sampler_Wind_Line,
                                                          windUV, 0);
                float3 rawWind    = (windSample.xyz - _Offset) * _Wind_Strength;
                float3 tangentWind = rawWind - dot(rawWind, sphereNormal) * sphereNormal;
                float  heightMask  = saturate(IN.uv.y * _Wind_Gradient);
                worldPos += tangentWind * heightMask;

                // Shadow bias — directional light uses _LightDirection,
                // spot/point lights use a per-vertex direction toward _LightPosition.
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 lightDir = normalize(_LightPosition - worldPos);
                #else
                    float3 lightDir = _LightDirection;
                #endif
                float4 posCS = TransformWorldToHClip(
                                    ApplyShadowBias(worldPos, finalNormal, lightDir));

                // Clamp to near plane (avoids shadow pancaking on reversed-Z platforms)
                #if UNITY_REVERSED_Z
                    posCS.z = min(posCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    posCS.z = max(posCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                OUT.positionHCS = posCS;
                OUT.uv          = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 fragShadow(VaryingsShadow IN) : SV_Target
            {
                // Alpha clip only — ColorMask 0 means no color is written
                half alpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).a;
                clip(alpha - 0.5);
                return 0;
            }
            ENDHLSL
        }
    }
}
