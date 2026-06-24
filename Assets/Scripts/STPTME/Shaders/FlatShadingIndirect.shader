Shader "Custom/FlatShadingIndirect"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Texture", 2D) = "white" {}
        [HideInInspector] _TreeMatrices ("Tree Matrices", Vector) = (0,0,0,0)
        [HideInInspector] _VisibleIndices ("Visible Indices", Vector) = (0,0,0,0)
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

            // Indirect-instancing buffers.
            StructuredBuffer<float4x4> _TreeMatrices;
            StructuredBuffer<uint> _VisibleIndices;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float2 uv : TEXCOORD1;
            };

            v2f vert(appdata_full v, uint instanceID : SV_InstanceID)
            {
                v2f o;

                // Fetch the global matrix index from the visible-indices buffer,
                // then fetch the actual world matrix.
                uint matrixIdx = _VisibleIndices[instanceID];
                float4x4 worldToObject = _TreeMatrices[matrixIdx];
                // Derive object-to-world by inversion — simpler: use worldToObject as TRS.
                // We need object->world for the normal Unity transforms.
                // Build objectToWorld from the stored matrix (which IS world).
                float4x4 objectToWorld = worldToObject;

                // Transform vertex.
                float4 worldVertex = mul(objectToWorld, v.vertex);
                o.pos = UnityWorldToClipPos(worldVertex);
                o.worldPos = worldVertex.xyz;
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