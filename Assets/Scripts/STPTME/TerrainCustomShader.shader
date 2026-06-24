Shader "Custom/TerrainCustomShader"
{
    Properties
    {
        [NoScaleOffset] _LayerDiffuseArray("Layer Diffuse Array", 2DArray) = "white" {}

        [NoScaleOffset] _SplatmapArray_T0("Splatmap Group0 Tier0", 2DArray) = "white" {}
        [NoScaleOffset] _SplatmapArray_T1("Splatmap Group0 Tier1", 2DArray) = "white" {}
        [NoScaleOffset] _SplatmapArray_T2("Splatmap Group0 Tier2", 2DArray) = "white" {}
        [NoScaleOffset] _SplatmapArray_T3("Splatmap Group0 Tier3", 2DArray) = "white" {}

        [NoScaleOffset] _SplatmapArray1_T0("Splatmap Group1 Tier0", 2DArray) = "black" {}
        [NoScaleOffset] _SplatmapArray1_T1("Splatmap Group1 Tier1", 2DArray) = "black" {}
        [NoScaleOffset] _SplatmapArray1_T2("Splatmap Group1 Tier2", 2DArray) = "black" {}
        [NoScaleOffset] _SplatmapArray1_T3("Splatmap Group1 Tier3", 2DArray) = "black" {}

        // Heightmap-derived world-space normal maps (Phase 2 NdotL system).
        // Independent tier mapping from splatmaps (per-LOD configurable in the bake settings).
        // Encoded as RGB8 = (worldNormal * 0.5 + 0.5) * 255.
        [NoScaleOffset] _NormalmapArray_T0("Normalmap Tier0", 2DArray) = "white" {}
        [NoScaleOffset] _NormalmapArray_T1("Normalmap Tier1", 2DArray) = "white" {}
        [NoScaleOffset] _NormalmapArray_T2("Normalmap Tier2", 2DArray) = "white" {}
        [NoScaleOffset] _NormalmapArray_T3("Normalmap Tier3", 2DArray) = "white" {}

        _SplatSliceIndex("Splat Slice Index", Float) = 0
        _SplatTier("Splat Tier", Float) = 0
        _NormalSliceIndex("Normal Slice Index", Float) = -1
        _NormalTier("Normal Tier", Float) = 0
        _UVOffsetScale("UV OffsetScale", Vector) = (0, 0, 1, 1)
        _NormalUVOffsetScale("Normal UV OffsetScale", Vector) = (0, 0, 1, 1)

        _LayerCount("Layer Count", Float) = 1
        _SplatGroupCount("Splat Group Count", Float) = 1

        [Header(Lighting)]
        // Hemisphere ambient: sky-facing surfaces get SkyAmbient, ground-facing get GroundAmbient.
        // Blended by how much the surface normal points away from the sphere center.
        // Update both from script during day-night cycle to match sun/sky colors.
        [HDR] _SkyAmbientColor("Sky Ambient", Color) = (0.05, 0.06, 0.08, 1)
        [HDR] _GroundAmbientColor("Ground Ambient", Color) = (0.02, 0.015, 0.01, 1)
        // Positive = blurrier (reduces sandpaper from afar). Start at 0, increase if distant terrain looks noisy.
        _TexLODBias("Texture LOD Bias", Range(0, 4)) = 1
        // 0 = smooth sphere normals (no terrain detail), 1 = full mesh normals (maximum terrain detail).
        _NormalStrength("Normal Strength", Range(0, 1)) = 0.5

        [Header(Canopy Noise)]
        _CanopyNoiseScale("Noise Tile Scale", Float) = 200.0
        // Controls how many Voronoi crown cells appear per noise tile.
        [Range(0, 1)] _CanopyNoiseStrength("Noise Perturbation Strength", Range(0, 1)) = 0.2

        [Header(Albedo Noise)]
        _AlbedoNoiseScale("Noise Scale", Float) = 0.3
        [Range(0, 1)] _AlbedoNoiseStrength("Noise Strength", Range(0, 1)) = 0.2

        [Header(Canopy Mask)]
        [NoScaleOffset] _CanopyMaskAtlas("Canopy Mask Atlas", 2D) = "white" {}
        _CanopyMaskAtlasSize("Mask Atlas Size (pixels)", Float) = 128

        // Uniform cell path: when USE_SINGLE_LAYER is active, this float selects
        // which layer index from _LayerDiffuseArray to sample (no splatmap needed).
        _UniformDominantLayer("Uniform Dominant Layer", Float) = 0

        [Header(Debug)]
        [KeywordEnum(Off, Albedo, NdotL, Normals)] _DEBUGMODE("Debug Mode", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ BATCHED_CHUNKS
            #pragma multi_compile _ _DEBUGMODE_ALBEDO _DEBUGMODE_NDOTL _DEBUGMODE_NORMALS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            #ifdef BATCHED_CHUNKS
                float4 uv1 : TEXCOORD1;
                float2 uv2 : TEXCOORD2; // (normalSliceIndex, normalTier) per vertex
                float2 uv3 : TEXCOORD3; // precomputed normal UV with normal-tile border compensation
                float2 uv4 : TEXCOORD4; // canopy mask UV (local chunk UV for atlas sampling)
            #endif
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 splatUV : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                nointerpolation float sliceIndex : TEXCOORD2;
                nointerpolation float tier : TEXCOORD3;
                float3 normalWS : TEXCOORD4;
                half3 vertexSH : TEXCOORD5;
            #ifdef USE_APV_PROBE_OCCLUSION
                float4 probeOcclusion : TEXCOORD6;
            #endif
            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord : TEXCOORD7;
            #endif
                half fogFactor : TEXCOORD8;
                nointerpolation float normalSliceIndex : TEXCOORD9;
                nointerpolation float normalTier : TEXCOORD10;
                float2 normalUV : TEXCOORD11;
            #ifdef BATCHED_CHUNKS
                // Canopy palette index (0-4) and canopy mode in y.
                // y = 0: no canopy, 1: palette-only canopy, 2: atlas-colour canopy.
                // Alpha: 1.0 on tree sites, tapers to 0 at edges via Chebyshev smoothing.
                // nointerpolation on index for crisp edges; normal interpolation on alpha for fade.
                nointerpolation float canopyIndex : TEXCOORD12;
                float canopyAlpha : TEXCOORD13;
                float2 canopyMaskUV : TEXCOORD14; // local chunk UV for canopy mask atlas sampling
            #endif
            };

            TEXTURE2D_ARRAY(_LayerDiffuseArray);
            SAMPLER(sampler_linear_repeat); // wrap sampler for triplanar diffuse tiling

            TEXTURE2D_ARRAY(_SplatmapArray_T0);
            SAMPLER(sampler_linear_clamp); // shared sampler for all splat/normal/diffuse arrays
            TEXTURE2D_ARRAY(_SplatmapArray_T1);
            TEXTURE2D_ARRAY(_SplatmapArray_T2);
            TEXTURE2D_ARRAY(_SplatmapArray_T3);

            TEXTURE2D_ARRAY(_SplatmapArray1_T0);
            TEXTURE2D_ARRAY(_SplatmapArray1_T1);
            TEXTURE2D_ARRAY(_SplatmapArray1_T2);
            TEXTURE2D_ARRAY(_SplatmapArray1_T3);

            TEXTURE2D_ARRAY(_NormalmapArray_T0);
            TEXTURE2D_ARRAY(_NormalmapArray_T1);
            TEXTURE2D_ARRAY(_NormalmapArray_T2);
            TEXTURE2D_ARRAY(_NormalmapArray_T3);

            TEXTURE2D(_CanopyMaskAtlas);

            CBUFFER_START(UnityPerMaterial)
                float _SplatSliceIndex;
                float _SplatTier;
                float _NormalSliceIndex;
                float _NormalTier;
                float4 _UVOffsetScale;
                float4 _NormalUVOffsetScale;
                float _LayerCount;
                float _SplatGroupCount;
                float4 _LayerTiling[8];
                half4 _SkyAmbientColor;
                half4 _GroundAmbientColor;
                float _TexLODBias;
                float _NormalStrength;
                float _CanopyNoiseScale;
                float _CanopyNoiseStrength;
                float _AlbedoNoiseScale;
                float _AlbedoNoiseStrength;
                float _CanopyMaskAtlasSize;
                float _CanopyMaskAtlasSizeInv;
                // Canopy overlay palette (runtime far-LOD batched chunks only).
                // Uploaded by ChunkMaterialManager.UploadCanopyPalette() at startup.
                // Indexed directly as _CanopyPalette[canopyIndex] (slot 0 -> [0], slot 4 -> [4]).
                // Stored in linear space; no texture fetch needed at runtime.
                float4 _CanopyPalette[5];
                float _UniformDominantLayer;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                VertexPositionInputs vertexInput = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionHCS = vertexInput.positionCS;
                OUT.positionWS = vertexInput.positionWS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                
            #ifdef BATCHED_CHUNKS
                OUT.splatUV = IN.uv1.xy;
                OUT.sliceIndex = IN.uv1.z;
                OUT.tier = IN.uv1.w;
                OUT.normalSliceIndex = IN.uv2.x;
                OUT.normalTier = IN.uv2.y;
                OUT.normalUV = IN.uv3;
                // UV0.xy carries canopy data: x = palette index (0-4), y = canopy mode.
                // Both written by ChunkBatcher canopy marking + smoothing passes.
                OUT.canopyIndex = IN.uv.x;
                OUT.canopyAlpha = IN.uv.y;
                // UV4.xy carries canopy mask local UV for atlas sampling.
                OUT.canopyMaskUV = IN.uv4;
            #else
                float2 uv = IN.uv * _UVOffsetScale.zw + _UVOffsetScale.xy;
                OUT.splatUV = clamp(uv, float2(1e-5, 1e-5), float2(1.0 - 1e-5, 1.0 - 1e-5));
                float2 normalUV = IN.uv * _NormalUVOffsetScale.zw + _NormalUVOffsetScale.xy;
                OUT.normalUV = clamp(normalUV, float2(1e-5, 1e-5), float2(1.0 - 1e-5, 1.0 - 1e-5));
                OUT.sliceIndex = _SplatSliceIndex;
                OUT.tier = _SplatTier;
                OUT.normalSliceIndex = _NormalSliceIndex;
                OUT.normalTier = _NormalTier;
            #endif

            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                OUT.shadowCoord = GetShadowCoord(vertexInput);
            #endif

                OUT.fogFactor = ComputeFogFactor(vertexInput.positionCS.z);

                OUTPUT_SH4(OUT.positionWS, OUT.normalWS.xyz, GetWorldSpaceNormalizeViewDir(OUT.positionWS), OUT.vertexSH, OUT.probeOcclusion);

                return OUT;
            }

            float4 SampleSplatGroup0(float2 splatUV, float slice, int tier)
            {
                if (tier <= 0) return SAMPLE_TEXTURE2D_ARRAY(_SplatmapArray_T0, sampler_linear_clamp, splatUV, slice);
                if (tier == 1) return SAMPLE_TEXTURE2D_ARRAY(_SplatmapArray_T1, sampler_linear_clamp, splatUV, slice);
                if (tier == 2) return SAMPLE_TEXTURE2D_ARRAY(_SplatmapArray_T2, sampler_linear_clamp, splatUV, slice);
                return SAMPLE_TEXTURE2D_ARRAY(_SplatmapArray_T3, sampler_linear_clamp, splatUV, slice);
            }

            float4 SampleSplatGroup1(float2 splatUV, float slice, int tier)
            {
                if (tier <= 0) return SAMPLE_TEXTURE2D_ARRAY(_SplatmapArray1_T0, sampler_linear_clamp, splatUV, slice);
                if (tier == 1) return SAMPLE_TEXTURE2D_ARRAY(_SplatmapArray1_T1, sampler_linear_clamp, splatUV, slice);
                if (tier == 2) return SAMPLE_TEXTURE2D_ARRAY(_SplatmapArray1_T2, sampler_linear_clamp, splatUV, slice);
                return SAMPLE_TEXTURE2D_ARRAY(_SplatmapArray1_T3, sampler_linear_clamp, splatUV, slice);
            }

            // Returns a unit world-space surface normal sampled from the per-tier heightmap normal map.
            float3 SampleHeightmapNormal(float2 splatUV, float slice, int tier)
            {
                float4 packed;
                if (tier <= 0)      packed = SAMPLE_TEXTURE2D_ARRAY(_NormalmapArray_T0, sampler_linear_clamp, splatUV, slice);
                else if (tier == 1) packed = SAMPLE_TEXTURE2D_ARRAY(_NormalmapArray_T1, sampler_linear_clamp, splatUV, slice);
                else if (tier == 2) packed = SAMPLE_TEXTURE2D_ARRAY(_NormalmapArray_T2, sampler_linear_clamp, splatUV, slice);
                else                packed = SAMPLE_TEXTURE2D_ARRAY(_NormalmapArray_T3, sampler_linear_clamp, splatUV, slice);
                // (n*0.5 + 0.5) → n * 2 - 1
                return normalize(packed.rgb * 2.0 - 1.0);
            }

            // Simple 2D hash function: maps a 2D integer grid cell to a pseudo-random float in [0,1].
            float Hash2D(float2 p)
            {
                float h = dot(p, float2(127.1, 311.7));
                return frac(sin(h) * 43758.5453);
            }

            // Pseudo-random 2D gradient: returns a unit-length direction vector for integer cell (i, j).
            float2 GradientHash(float2 cell)
            {
                float h = dot(cell, float2(127.1, 311.7));
                float angle = frac(sin(h) * 43758.5453) * 6.283185; // 2*PI
                return float2(cos(angle), sin(angle));
            }

            // Perlin 2D gradient noise. Returns smooth values in [-1, 1] with no visible grid cells.
            // Each integer lattice cell has a pseudo-random gradient; the output is the dot product
            // of that gradient with the fractional offset, smoothly blended with bilinear hermite
            // interpolation over the 2×2 neighborhood.
            float PerlinNoise(float2 uv)
            {
                float2 i = floor(uv);
                float2 f = frac(uv);

                // Hermite smoothstep
                float2 u = f * f * (3.0 - 2.0 * f);

                float2 g00 = GradientHash(i + float2(0, 0));
                float2 g10 = GradientHash(i + float2(1, 0));
                float2 g01 = GradientHash(i + float2(0, 1));
                float2 g11 = GradientHash(i + float2(1, 1));

                float2 d00 = f - float2(0, 0);
                float2 d10 = f - float2(1, 0);
                float2 d01 = f - float2(0, 1);
                float2 d11 = f - float2(1, 1);

                float n00 = dot(g00, d00);
                float n10 = dot(g10, d10);
                float n01 = dot(g01, d01);
                float n11 = dot(g11, d11);

                float nx0 = lerp(n00, n10, u.x);
                float nx1 = lerp(n01, n11, u.x);

                return lerp(nx0, nx1, u.y); // output in [-1, 1]
            }

            // Worley (cellular) noise: returns a height value in [0,1] where 1 = cell centre
            // (crown peak) and 0 = cell boundary (valley between crowns). Each Voronoi cell
            // naturally maps to one tree crown dome, giving a convincing canopy silhouette when
            // used as a bump height field and differentiated for normal perturbation.
            // The cell feature point is jittered by Hash2D so crowns are irregularly spaced.
            float CanopyWorley(float2 uv)
            {
                float2 cell = floor(uv);
                float minDist = 1e9;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    float2 neighbor = cell + float2(dx, dy);
                    // Two independent hash channels for x/y jitter of the feature point.
                    float2 pt = neighbor + float2(Hash2D(neighbor), Hash2D(neighbor + float2(97.3, 31.7)));
                    minDist = min(minDist, length(uv - pt));
                }
                // Invert and scale: 0 at cell edge (~0.55 apart), 1 at cell centre.
                // Factor 1.8 maps the typical min-distance range [0, ~0.55] → [0, ~1].
                return 1.0 - saturate(minDist * 1.8);
            }

            // Triplanar sampling: blend texture from 3 projection planes weighted by surface normal.
            // Uses explicit gradients (ddx/ddy) for correct mip selection at glancing angles.
            float4 SampleLayerDiffuse(int layerIndex, float3 positionWS, float3 normalWS)
            {
                float4 layerParams = _LayerTiling[layerIndex];
                float2 tileSize = max(layerParams.xy, float2(1e-5, 1e-5));
                float2 tileOffset = layerParams.zw;

                float3 blend = pow(abs(normalWS), 3.0);
                blend /= (blend.x + blend.y + blend.z + 1e-6);

                // Optional LOD bias: scale gradients to shift mip level
                float gradScale = exp2(_TexLODBias);

                // YZ projection
                float2 uvYZ = positionWS.yz / tileSize + tileOffset;
                float2 duvYZ_dx = ddx(positionWS.yz / tileSize) * gradScale;
                float2 duvYZ_dy = ddy(positionWS.yz / tileSize) * gradScale;
                float4 result = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LayerDiffuseArray, sampler_linear_repeat, uvYZ, layerIndex, duvYZ_dx, duvYZ_dy) * blend.x;

                // XZ projection
                float2 uvXZ = positionWS.xz / tileSize + tileOffset;
                float2 duvXZ_dx = ddx(positionWS.xz / tileSize) * gradScale;
                float2 duvXZ_dy = ddy(positionWS.xz / tileSize) * gradScale;
                result += SAMPLE_TEXTURE2D_ARRAY_GRAD(_LayerDiffuseArray, sampler_linear_repeat, uvXZ, layerIndex, duvXZ_dx, duvXZ_dy) * blend.y;

                // XY projection
                float2 uvXY = positionWS.xy / tileSize + tileOffset;
                float2 duvXY_dx = ddx(positionWS.xy / tileSize) * gradScale;
                float2 duvXY_dy = ddy(positionWS.xy / tileSize) * gradScale;
                result += SAMPLE_TEXTURE2D_ARRAY_GRAD(_LayerDiffuseArray, sampler_linear_repeat, uvXY, layerIndex, duvXY_dx, duvXY_dy) * blend.z;

                return result;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float uniformDL = _UniformDominantLayer;
                float3 blended;
                float3 normalWS;
                float3 terrainNormal;

                if (IN.normalSliceIndex >= 0.0)
                {
                    int nTier = clamp((int)round(IN.normalTier), 0, 3);
                    terrainNormal = SampleHeightmapNormal(IN.normalUV, IN.normalSliceIndex, nTier);
                }
                else
                {
                    terrainNormal = normalize(IN.normalWS);
                }
                float3 sphereNormal = normalize(IN.positionWS);
                normalWS = normalize(lerp(sphereNormal, terrainNormal, _NormalStrength));

                if (uniformDL >= 0.0)
                {
                    // Single-layer path: skip splatmap entirely, sample the dominant layer's diffuse.
                    // _UniformDominantLayer is set per-chunk by ChunkMaterialManager on the material;
                    // values < 0 mean "use the standard multi-layer path" (non-uniform cell).
                    int dl = clamp((int)round(uniformDL), 0, (int)round(_LayerCount) - 1);
                    blended = SampleLayerDiffuse(dl, IN.positionWS, normalWS).rgb;
                }
                else
                {
                    // Multi-layer (standard) path: sample splatmap, blend 4+ layers.
                    int tier = clamp((int)round(IN.tier), 0, 3);
                    float slice = IN.sliceIndex;
                    float2 splatUV = IN.splatUV;

                    float4 weights0 = SampleSplatGroup0(splatUV, slice, tier);
                    float4 weights1 = (_SplatGroupCount > 1.5)
                        ? SampleSplatGroup1(splatUV, slice, tier)
                        : float4(0, 0, 0, 0);

                    float weights[8];
                    weights[0] = max(weights0.r, 0.0);
                    weights[1] = max(weights0.g, 0.0);
                    weights[2] = max(weights0.b, 0.0);
                    weights[3] = max(weights0.a, 0.0);
                    weights[4] = max(weights1.r, 0.0);
                    weights[5] = max(weights1.g, 0.0);
                    weights[6] = max(weights1.b, 0.0);
                    weights[7] = max(weights1.a, 0.0);

                    int maxLayers = clamp((int)round(_LayerCount), 1, 8);

                    float totalWeight = 0.0;
                    for (int i = 0; i < maxLayers; i++)
                        totalWeight += weights[i];

                    totalWeight = max(totalWeight, 1e-5);

                    blended = float3(0, 0, 0);
                    for (int j = 0; j < 8; j++)
                    {
                        float w = (j < maxLayers) ? (weights[j] / totalWeight) : 0.0;
                        if (w <= 0.0001) continue;

                        float3 layerColor = SampleLayerDiffuse(j, IN.positionWS, normalWS).rgb;
                        blended += layerColor * w;
                    }
                }

                // Cheap albedo noise: modulate the final blended color in world space.
                // Uses Perlin gradient noise to break up repeated triplanar texture patterns
                // without visible grid artifacts.
                // The noise amount is scaled by surface brightness: bright surfaces (snow)
                // get almost no noise so they don't look dirty; dark surfaces get full noise.
                if (_AlbedoNoiseStrength > 0.0001)
                {
                    float brightness = dot(blended, float3(0.299, 0.587, 0.114));
                    float noiseAmount = lerp(_AlbedoNoiseStrength * 1.4, _AlbedoNoiseStrength * 0.15, brightness);

                    float2 noiseUV = IN.positionWS.xz * _AlbedoNoiseScale;
                    // PerlinNoise returns in [-1, 1]; remap to [0, 1].
                    float n1 = PerlinNoise(noiseUV) * 0.5 + 0.5;
                    float n2 = PerlinNoise(noiseUV * 2.0 + float2(17.0, 31.0)) * 0.5 + 0.5;

                    float tint = lerp(1.0 - noiseAmount, 1.0 + noiseAmount, n1);
                    float tint2 = lerp(1.0 - noiseAmount * 0.35, 1.0 + noiseAmount * 0.35, n2);

                    blended *= lerp(tint, tint2, 0.35);
                }

                // Far-LOD canopy overlay: smooth blend with alpha fade (batched path only).
                // Samples a per-chunk mask texture from the canopy mask atlas using UV4.
                // The mask provides smooth soft-edged tree footprints; the palette index
                // selects the canopy colour. Bilinear filtering on the atlas gives smooth
                // transitions even on very low-poly (4-vertex) chunks.
                #ifdef BATCHED_CHUNKS
                {
                    int ci = round(IN.canopyIndex);
                    float canopyMode = IN.canopyAlpha;
                    if (canopyMode > 0.001)
                    {
                        float blendAlpha = 0.0;
                        float3 canopyColor = float3(0.0, 0.0, 0.0);

                        if (canopyMode > 1.5)
                        {
                            float4 canopySample = SAMPLE_TEXTURE2D(_CanopyMaskAtlas, sampler_linear_clamp, IN.canopyMaskUV);
                            blendAlpha = canopySample.a;
                            canopyColor = canopySample.rgb;
                        }
                        else if (ci >= 0 && ci < 5)
                        {
                            blendAlpha = 1.0;
                            canopyColor = _CanopyPalette[ci].rgb;
                        }

                        blended = lerp(blended, canopyColor, blendAlpha);

                        // Canopy normal perturbation: treat Worley noise as a height field and
                        // differentiate it with finite differences to get a surface gradient.
                        // The gradient tells us which way the surface slopes at each pixel,
                        // producing proper shadowed peaks (crown centres) and valleys (gaps
                        // between crowns) instead of a uniform directional tilt.
                        // Each Voronoi cell ≈ one tree crown dome; the pattern is shifted
                        // per chunk via the seed offset so adjacent chunks don't tile visibly.
                        if (_CanopyNoiseStrength > 0.001 && blendAlpha > 0.001)
                        {
                            float seed = IN.sliceIndex * 7.0 + IN.tier * 13.0;
                            // Shift UV per chunk so Worley cells don't repeat at chunk boundaries.
                            float2 noiseUV = IN.splatUV * _CanopyNoiseScale
                                           + float2(frac(seed * 0.1370), frac(seed * 0.2510));

                            // Crown density: scale the Worley input independently from the
                            // cells = sparser crowns matching a low-density tree distribution.
                            float2 worleyUV = noiseUV;

                            // Finite-difference gradient: sample height at centre + 2 offsets.
                            // eps is in worleyUV space so gradient magnitude stays consistent.
                            float eps = 0.02;
                            float h0  = CanopyWorley(worleyUV);
                            float hDx = CanopyWorley(worleyUV + float2(eps, 0.0));
                            float hDy = CanopyWorley(worleyUV + float2(0.0, eps));
                            float dHdx = (hDx - h0) / eps;
                            float dHdy = (hDy - h0) / eps;

                            float3 tangent = normalize(cross(normalWS, float3(0, 1, 0)));
                            if (dot(tangent, tangent) < 0.1) tangent = normalize(cross(normalWS, float3(1, 0, 0)));
                            float3 bitangent = cross(normalWS, tangent);

                            // Negate gradient: surface tilts away from the uphill direction.
                            float perturbStrength = _CanopyNoiseStrength * blendAlpha;
                            normalWS += (-tangent * dHdx - bitangent * dHdy) * perturbStrength;
                            normalWS = normalize(normalWS);
                        }
                    }
                }
                #endif

                // URP Standard PBR Lighting matching built-in Unity Terrain Lit
                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.normalWS = normalWS; // already normalized
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    inputData.shadowCoord = IN.shadowCoord;
                #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                    inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                #else
                    inputData.shadowCoord = float4(0, 0, 0, 0);
                #endif

                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionHCS);
                inputData.fogCoord = InitializeInputDataFog(float4(IN.positionWS, 1.0), IN.fogFactor);
                inputData.shadowMask = half4(1, 1, 1, 1);

                // Hemisphere ambient: blend sky vs ground color by how much the normal
                // points away from the sphere center (works correctly on all 6 cube faces).
                // Combined with the SH probe for any remaining directional information.
                half3 sh;
                #if !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
                    sh = SAMPLE_GI(IN.vertexSH,
                        GetAbsolutePositionWS(IN.positionWS),
                        normalWS,
                        inputData.viewDirectionWS,
                        IN.positionHCS.xy,
                        IN.probeOcclusion,
                        inputData.shadowMask);
                #else
                    sh = SampleSHPixel(IN.vertexSH, normalWS);
                #endif
                // skyFactor: 1 = surface faces open sky, 0 = vertical, -1 = overhang/cave
                half skyFactor = dot(normalWS, normalize(IN.positionWS)) * 0.5h + 0.5h;
                inputData.bakedGI = sh + lerp(_GroundAmbientColor.rgb, _SkyAmbientColor.rgb, skyFactor);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = blended;
                surfaceData.alpha = 1.0;
                surfaceData.metallic = 0.0;
                surfaceData.specular = half3(0, 0, 0);
                surfaceData.smoothness = 0.2; // Mild smoothness matching packed terrain standard
                surfaceData.normalTS = half3(0, 0, 1);
                surfaceData.emission = half3(0, 0, 0);
                surfaceData.occlusion = 1.0;

                // Debug visualization modes
                #if defined(_DEBUGMODE_ALBEDO)
                    return half4(blended, 1);
                #elif defined(_DEBUGMODE_NDOTL)
                    Light dbgLight2 = GetMainLight();
                    half ndotl = saturate(dot(normalWS, dbgLight2.direction));
                    return half4(ndotl, ndotl, ndotl, 1);
                #elif defined(_DEBUGMODE_NORMALS)
                    return half4(normalWS * 0.5 + 0.5, 1);
                #endif

                half4 finalColor = UniversalFragmentPBR(inputData, surfaceData);
                finalColor.rgb = MixFog(finalColor.rgb, inputData.fogCoord);

                return finalColor;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ZTest LEqual
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile _ BATCHED_CHUNKS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
            };

            struct DepthVaryings
            {
                float4 positionHCS : SV_POSITION;
            };

            DepthVaryings DepthVert(DepthAttributes IN)
            {
                DepthVaryings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 DepthFrag(DepthVaryings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            #pragma multi_compile _ BATCHED_CHUNKS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct DepthNormalsAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct DepthNormalsVaryings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
            };

            DepthNormalsVaryings DepthNormalsVert(DepthNormalsAttributes IN)
            {
                DepthNormalsVaryings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 DepthNormalsFrag(DepthNormalsVaryings IN) : SV_Target
            {
                float3 normal = normalize(IN.normalWS);
                return half4(normal, 0);
            }
            ENDHLSL
        }
    }
}
