Shader "CoreAI/Rbx/Textured Surface"
{
    Properties
    {
        [MainTexture] _BaseMap("Color Map", 2D) = "white" {}
        [MainColor] _BaseColor("Part Color", Color) = (1,1,1,1)
        [HideInInspector] _Color("Part Color Compatibility", Color) = (1,1,1,1)
        [HideInInspector] _MaterialColor("Intrinsic Material Color", Color) = (1,1,1,1)
        [HideInInspector] _PartColorInfluence("Part Color Influence", Range(0,1)) = 0.75
        [HideInInspector] _NeutralDefaultPartColor("Neutral Default Part Color", Float) = 1
        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Strength", Range(0,2)) = 1.0
        _RoughnessMap("Roughness Map", 2D) = "white" {}
        _RoughnessScale("Roughness Scale", Range(0,2)) = 1.0
        [Toggle] _InvertRoughness("Map Stores Smoothness", Float) = 0
        _MetallicMap("Metalness Map", 2D) = "black" {}
        _OcclusionMap("Ambient Occlusion Map", 2D) = "white" {}
        _CavityStrength("Cavity Strength", Range(0,1)) = 0
        _MetalAlbedoLift("Metal Albedo Lift", Range(0,1)) = 0
        _TextureScale("Texture Scale", Float) = 1
        _TextureAspect("Texture Aspect", Float) = 1
        [HideInInspector] _Cutoff("Alpha Cutoff", Range(0,1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "UniversalMaterialType" = "Lit"
        }
        LOD 250

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex RbxTexturedVertex
            #pragma fragment RbxTexturedFragment
            #pragma multi_compile_local_fragment _ _RBX_METALLIC_MAP
            #pragma multi_compile_local_fragment _ _RBX_OCCLUSION_MAP
            #pragma multi_compile_local_fragment _ _RBX_NORMAL_DIRECTX
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            // WHY: URP 17 renders Forward+ (PC_Renderer m_RenderingMode: 2) and delivers the main
            // light through the clustered light loop. Without this keyword the pass compiles the
            // non-clustered variant and every Rbx part is lit by ambient alone — no sun, no shadows.
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);
            TEXTURE2D(_RoughnessMap);
            SAMPLER(sampler_RoughnessMap);
            TEXTURE2D(_MetallicMap);
            SAMPLER(sampler_MetallicMap);
            TEXTURE2D(_OcclusionMap);
            SAMPLER(sampler_OcclusionMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _Color;
                half4 _MaterialColor;
                float _PartColorInfluence;
                float _TextureScale;
                float _TextureAspect;
                float _BumpScale;
                float _RoughnessScale;
                float _InvertRoughness;
                float _CavityStrength;
                float _MetalAlbedoLift;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float3 positionAligned : TEXCOORD2;
                float3 normalAligned : TEXCOORD3;
                float2 halfExtentXZ : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            struct RbxProjectionCoordinates
            {
                float2 uv;
                float2 derivativeX;
                float2 derivativeY;
            };

            float3 RbxTextureObjectAxisScale()
            {
                float3x3 objectToWorld = (float3x3)GetObjectToWorldMatrix();
                return float3(length(mul(objectToWorld, float3(1.0, 0.0, 0.0))),
                    length(mul(objectToWorld, float3(0.0, 1.0, 0.0))),
                    length(mul(objectToWorld, float3(0.0, 0.0, 1.0))));
            }

            // WHY: a flat face interpolates one constant normal, so its normal does not change from
            // pixel to pixel beyond float rounding, while a curved mesh turns its normal by the pixel
            // footprint divided by its radius. Comparing the two changes instead of testing an absolute
            // threshold keeps a close-up of a huge ball curved at any resolution: every surface whose
            // radius is under RBX_FLAT_FACE_RADIUS aligned metres is curved. fwidth is uniform across a
            // 2x2 quad, so the branch is coherent.
            static const float RBX_FLAT_FACE_RADIUS = 500.0;
            static const float RBX_FLAT_FACE_ROUNDING = 0.000001;

            // WHY: components below this floor get no weight and the fourth power sharpens the rest,
            // so the blend between two projections spans about +-11 degrees around each 45-degree
            // boundary (weights 5%-95%) instead of the old +-4 degree band that drew a hard cross on
            // every ball and a seam down every drum.
            static const float RBX_CURVED_BLEND_FLOOR = 0.3;

            // WHY: a weight this small changes the colour by under 3% but still costs a full sample
            // set, so it is dropped before renormalising. A ball then samples one projection on about
            // half of its surface, two on about 40% and three only near the eight triple-axis points
            // (about 5%).
            static const float RBX_CURVED_WEIGHT_CUTOFF = 0.025;

            bool RbxIsFlatFace(float3 geometricNormalAligned, float3 positionAligned)
            {
                float3 normalChange = fwidth(geometricNormalAligned);
                float3 positionChange = fwidth(positionAligned);
                return normalChange.x + normalChange.y + normalChange.z <
                    RBX_FLAT_FACE_ROUNDING + (positionChange.x + positionChange.y + positionChange.z)
                    / RBX_FLAT_FACE_RADIUS;
            }

            float3 RbxCurvedAxisWeights(float3 geometricNormalAligned)
            {
                float3 weights = max(abs(geometricNormalAligned) - RBX_CURVED_BLEND_FLOOR, 0.0);
                weights *= weights;
                weights *= weights;
                weights /= max(weights.x + weights.y + weights.z, 0.00001);
                weights *= step(RBX_CURVED_WEIGHT_CUTOFF, weights);
                return weights / max(weights.x + weights.y + weights.z, 0.00001);
            }

            float2 RbxFaceUv(float3 positionAligned, float2 halfExtentXZ, float3 faceNormal,
                float2 uvScale)
            {
                // WHY: U runs to the viewer's right and V up the face, so no block or wedge side shows
                // the map mirrored or upside down. The four sides are unrolled round the part (+X, +Z,
                // -X, -Z) with offsets that make three vertical edges continuous, so a brick course or
                // a plank carries on round a corner instead of reflecting at it; the one wrap seam sits
                // on the +X/-Z edge. Top and bottom stay anchored at the part centre with no size term,
                // so floor plates of different sizes laid side by side keep one grid.
                float horizontalLength = length(faceNormal.xz);
                float3 uAxis = float3(-1.0, 0.0, 0.0);
                float3 vAxis = float3(0.0, 0.0, faceNormal.y >= 0.0 ? -1.0 : 1.0);
                float uOffset = 0.0;
                if (horizontalLength > 0.001)
                {
                    // WHY: a slope gets its own frame instead of borrowing an axis projection, which
                    // stretched a 45-degree wedge face by 1.41 and tied two projections exactly on it,
                    // painting every such wedge with a ghosted double image.
                    uAxis = float3(-faceNormal.z, 0.0, faceNormal.x) / horizontalLength;
                    vAxis = (float3(0.0, 1.0, 0.0) - faceNormal * faceNormal.y) / horizontalLength;
                    float hx = halfExtentXZ.x;
                    float hz = halfExtentXZ.y;
                    if (abs(faceNormal.x) > abs(faceNormal.z))
                    {
                        uOffset = faceNormal.x > 0.0 ? hz : 2.0 * hx + 3.0 * hz;
                    }
                    else
                    {
                        uOffset = faceNormal.z > 0.0 ? hx + 2.0 * hz : 3.0 * hx + 4.0 * hz;
                    }
                }

                // WHY: the map repeats every whole tile, so only the fractional part of the edge
                // offset matters. Folding it before the position term keeps U small on a huge part,
                // where an offset of several hundred tiles would eat the float precision of the UV.
                float2 edgeOffset = float2(frac(uOffset * uvScale.x), 0.0);
                return float2(dot(positionAligned, uAxis), dot(positionAligned, vAxis)) * uvScale
                    + edgeOffset;
            }

            // WHY: the packaged ambientCG metal colour maps are photographs whose linear reflectance
            // sits around 0.11-0.16, while every real metal reflects 0.5 or more; used as-is as F0
            // they rendered Metal, DiamondPlate and CorrodedMetal as near-black mirrors. Those entries
            // set _MetalAlbedoLift and move their metallic texels that share of the way toward the
            // brightest shade of their own hue, which keeps the tint. The lift fades out below a
            // brightness of 0.1 so dark rust and grime stay dark instead of turning into saturated
            // hue noise, and it is zero for every other entry, so an imported metal whose map is
            // already calibrated is left alone.
            half3 RbxLiftMetalReflectance(half3 albedo, half metallic, half lift)
            {
                half brightest = max(albedo.r, max(albedo.g, albedo.b));
                half amount = lift * saturate(metallic) * smoothstep(0.02h, 0.1h, brightest);
                half3 fullTint = albedo / max(brightest, 0.02h);
                return lerp(albedo, fullTint, amount);
            }

            RbxProjectionCoordinates RbxProjectionData(float2 uv)
            {
                RbxProjectionCoordinates projection;
                projection.uv = uv;
                projection.derivativeX = ddx(uv);
                projection.derivativeY = ddy(uv);
                return projection;
            }

            float3 RbxNormalFromDerivatives(float3 normalWS, float3 positionDerivativeX,
                float3 positionDerivativeY, float2 uvDerivativeX, float2 uvDerivativeY,
                half3 normalTS)
            {
                float3 perpendicularX = cross(positionDerivativeY, normalWS);
                float3 perpendicularY = cross(normalWS, positionDerivativeX);
                float3 tangentWS = perpendicularX * uvDerivativeX.x +
                    perpendicularY * uvDerivativeY.x;
                float3 bitangentWS = perpendicularX * uvDerivativeX.y +
                    perpendicularY * uvDerivativeY.y;
                // WHY: the cotangent vectors scale with the pixel footprint times the UV rate, so
                // their squared length is far below 1e-6 at ordinary distances. The old 1e-6 floor
                // therefore won and shrank the tangent frame: a packaged normal map kept about 5% of
                // its strength at 10 m and next to nothing at 1 m. The floor now only guards zero.
                float inverseScale = rsqrt(max(max(dot(tangentWS, tangentWS),
                    dot(bitangentWS, bitangentWS)), 1e-30));
                tangentWS *= inverseScale;
                bitangentWS *= inverseScale;
                return normalize(tangentWS * normalTS.x + bitangentWS * normalTS.y +
                    normalWS * normalTS.z);
            }

            void RbxAccumulateTextureProjection(RbxProjectionCoordinates projection, float weight,
                float3 baseNormalWS, float3 positionDerivativeX, float3 positionDerivativeY,
                inout half3 textureColor, inout half roughness, inout float3 mappedNormalWS,
                inout half metallic, inout half occlusion)
            {
                half3 axisColor = SAMPLE_TEXTURE2D_GRAD(_BaseMap, sampler_BaseMap, projection.uv,
                    projection.derivativeX, projection.derivativeY).rgb;
                half axisRoughnessSample = SAMPLE_TEXTURE2D_GRAD(_RoughnessMap,
                    sampler_RoughnessMap,
                    projection.uv, projection.derivativeX, projection.derivativeY).r;
                half axisRoughness = lerp(axisRoughnessSample, 1.0h - axisRoughnessSample,
                    saturate(_InvertRoughness));
                axisRoughness = saturate(axisRoughness * _RoughnessScale);
                half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_BumpMap,
                    sampler_BumpMap, projection.uv, projection.derivativeX,
                    projection.derivativeY), _BumpScale);
                #if defined(_RBX_NORMAL_DIRECTX)
                    normalTS.y = -normalTS.y;
                #endif
                float3 axisNormalWS = RbxNormalFromDerivatives(baseNormalWS, positionDerivativeX,
                    positionDerivativeY, projection.derivativeX, projection.derivativeY, normalTS);
                half axisMetallic = 0.0h;
                #if defined(_RBX_METALLIC_MAP)
                    axisMetallic = SAMPLE_TEXTURE2D_GRAD(_MetallicMap, sampler_MetallicMap,
                        projection.uv, projection.derivativeX, projection.derivativeY).r;
                #endif
                half axisOcclusion = 1.0h;
                #if defined(_RBX_OCCLUSION_MAP)
                    axisOcclusion = SAMPLE_TEXTURE2D_GRAD(_OcclusionMap, sampler_OcclusionMap,
                        projection.uv, projection.derivativeX, projection.derivativeY).r;
                #endif

                textureColor += axisColor * weight;
                roughness += axisRoughness * weight;
                mappedNormalWS += axisNormalWS * weight;
                metallic += axisMetallic * weight;
                occlusion += axisOcclusion * weight;
            }

            Varyings RbxTexturedVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 objectAxisScale = RbxTextureObjectAxisScale();
                output.positionAligned = input.positionOS.xyz * objectAxisScale;
                // WHY: positions are stretched by the part size, so the normal is divided by it (the
                // inverse transpose). The raw mesh normal put the slope of a 6x6x12 wedge at 45
                // degrees and chose the wrong projection on every stretched ball or wedge.
                output.normalAligned = normalize(input.normalOS / max(objectAxisScale, 0.00001));
                // WHY: the face unrolling needs the part's half size across X and Z. Every primitive
                // spans +-0.5 on those axes (block, wedge, ball and the cylinder's round section), so
                // half the axis scale is that half size. The cylinder mesh spans +-1 along its own Y,
                // which is why Y is not passed: top and bottom faces carry no size term.
                output.halfExtentXZ = objectAxisScale.xz * 0.5;
                return output;
            }

            half4 RbxTexturedFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uvScale = float2(_TextureScale / max(_TextureAspect, 0.0001),
                    _TextureScale);
                float3 geometricNormalAligned = normalize(input.normalAligned);
                float3 positionAligned = input.positionAligned;
                float2 halfExtentXZ = input.halfExtentXZ;
                // WHY: a flat face (block side, wedge slope, drum cap) reads exactly one map in its own
                // upright frame; only curved surfaces blend the three axis projections.
                bool flatFace = RbxIsFlatFace(geometricNormalAligned, positionAligned);
                float faceWeight = flatFace ? 1.0 : 0.0;
                float3 projectionWeights = flatFace
                    ? float3(0.0, 0.0, 0.0)
                    : RbxCurvedAxisWeights(geometricNormalAligned);
                float3 axisSigns = float3(geometricNormalAligned.x >= 0.0 ? 1.0 : -1.0,
                    geometricNormalAligned.y >= 0.0 ? 1.0 : -1.0,
                    geometricNormalAligned.z >= 0.0 ? 1.0 : -1.0);
                RbxProjectionCoordinates projectionFace = RbxProjectionData(RbxFaceUv(
                    positionAligned, halfExtentXZ, geometricNormalAligned, uvScale));
                RbxProjectionCoordinates projectionX = RbxProjectionData(RbxFaceUv(
                    positionAligned, halfExtentXZ, float3(axisSigns.x, 0.0, 0.0), uvScale));
                RbxProjectionCoordinates projectionY = RbxProjectionData(RbxFaceUv(
                    positionAligned, halfExtentXZ, float3(0.0, axisSigns.y, 0.0), uvScale));
                RbxProjectionCoordinates projectionZ = RbxProjectionData(RbxFaceUv(
                    positionAligned, halfExtentXZ, float3(0.0, 0.0, axisSigns.z), uvScale));
                float3 positionDerivativeX = ddx(input.positionWS);
                float3 positionDerivativeY = ddy(input.positionWS);
                float3 baseNormalWS = normalize(input.normalWS);
                half3 textureColor = half3(0.0h, 0.0h, 0.0h);
                half roughness = 0.0h;
                float3 mappedNormalWS = float3(0.0, 0.0, 0.0);
                half metallic = 0.0h;
                half occlusion = 0.0h;

                UNITY_BRANCH if (faceWeight > 0.0)
                {
                    RbxAccumulateTextureProjection(projectionFace, faceWeight, baseNormalWS,
                        positionDerivativeX, positionDerivativeY, textureColor, roughness,
                        mappedNormalWS, metallic, occlusion);
                }
                UNITY_BRANCH if (projectionWeights.x > 0.0)
                {
                    RbxAccumulateTextureProjection(projectionX, projectionWeights.x, baseNormalWS,
                        positionDerivativeX, positionDerivativeY, textureColor, roughness,
                        mappedNormalWS, metallic, occlusion);
                }
                UNITY_BRANCH if (projectionWeights.y > 0.0)
                {
                    RbxAccumulateTextureProjection(projectionY, projectionWeights.y, baseNormalWS,
                        positionDerivativeX, positionDerivativeY, textureColor, roughness,
                        mappedNormalWS, metallic, occlusion);
                }
                UNITY_BRANCH if (projectionWeights.z > 0.0)
                {
                    RbxAccumulateTextureProjection(projectionZ, projectionWeights.z, baseNormalWS,
                        positionDerivativeX, positionDerivativeY, textureColor, roughness,
                        mappedNormalWS, metallic, occlusion);
                }

                half3 partModulation = lerp(half3(1.0h, 1.0h, 1.0h),
                    saturate(_Color.rgb * 1.15h), saturate(_PartColorInfluence));
                half3 materialAlbedo = textureColor * _MaterialColor.rgb;
                #if defined(_RBX_METALLIC_MAP)
                    materialAlbedo = RbxLiftMetalReflectance(materialAlbedo, metallic,
                        (half)_MetalAlbedoLift);
                #endif
                #if defined(_RBX_OCCLUSION_MAP)
                    // WHY: a normal map keeps its relief in one- or two-texel bevels that the mip
                    // chain averages away, so beyond a few metres mortar joints and cobble gaps
                    // stopped reading as recessed. An occlusion map averages correctly under
                    // mipmapping, so letting it darken the albedo (the direct light as well as the
                    // ambient URP already scales) keeps grooves visible at every distance. Packaged
                    // entries set the strength for the occlusion map baked from their normal map;
                    // imported sets keep 0, where their AO still affects only the ambient light.
                    materialAlbedo *= lerp(1.0h, occlusion, (half)saturate(_CavityStrength));
                #endif
                half3 albedo = materialAlbedo * partModulation;
                float3 normalWS = normalize(mappedNormalWS);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo;
                surfaceData.specular = half3(0.04h, 0.04h, 0.04h);
                surfaceData.metallic = metallic;
                surfaceData.smoothness = saturate(1.0h - roughness);
                surfaceData.normalTS = half3(0.0h, 0.0h, 1.0h);
                surfaceData.emission = half3(0.0h, 0.0h, 0.0h);
                surfaceData.occlusion = occlusion;
                surfaceData.alpha = 1.0h;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = ComputeFogFactor(input.positionCS.z);
                inputData.vertexLighting = VertexLighting(input.positionWS, normalWS);
                inputData.bakedGI = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = 1.0h;
                return color;
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
    }

    Fallback "CoreAI/Rbx/Material Fallback"
}
