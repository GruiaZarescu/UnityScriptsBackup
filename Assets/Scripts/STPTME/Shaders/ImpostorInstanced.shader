Shader "Custom/ImpostorInstanced"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Texture", 2D) = "white" {}
        _LODWidthMultipliers ("LOD Width Multipliers", Vector) = (1,1.15,1.35,1.6)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Color;
            float4 _LODWidthMultipliers[8];

            // Instance data buffer (filled by CSExpandBlotches kernel)
            struct InstanceData
            {
                float3 worldPos;
                uint packedMeta; // bits: 0-7=protoIndex, 8-15=chunkLOD, 16-23=rotQ, 24-31=scaleQ
                uint seed;
            };
            StructuredBuffer<InstanceData> _InstanceOutputBuffer;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float2 uv : TEXCOORD1;
            };

            v2f vert(appdata_full v, uint instanceID : SV_InstanceID)
            {
                v2f o;
                InstanceData inst = _InstanceOutputBuffer[instanceID];

                uint chunkLOD = (inst.packedMeta >> 8) & 0xFF;
                uint rotQ = (inst.packedMeta >> 16) & 0xFF;
                uint scaleQ = (inst.packedMeta >> 24) & 0xFF;

                float rotation = rotQ / 255.0 * 6.283185;
                float scale = lerp(0.5, 2.0, scaleQ / 255.0);
                float widthMult = _LODWidthMultipliers[min(chunkLOD, 7)];

                // Build rotation matrix around Y (local up on sphere = radial)
                float s, c; sincos(rotation, s, c);
                float3 localPos = v.vertex.xyz;
                localPos.xz *= widthMult * scale;
                float3 rotatedPos;
                rotatedPos.x = localPos.x * c - localPos.z * s;
                rotatedPos.z = localPos.x * s + localPos.z * c;
                rotatedPos.y = localPos.y * scale;
                float3 worldPos = inst.worldPos + rotatedPos;

                o.pos = UnityWorldToClipPos(worldPos);
                o.worldPos = worldPos;
                o.uv = v.texcoord;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 faceNormal = normalize(cross(ddy(i.worldPos), ddx(i.worldPos)));
                float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
                float diff = max(0, dot(faceNormal, lightDir));
                fixed4 tex = tex2D(_MainTex, i.uv);
                return tex * _Color * diff;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}