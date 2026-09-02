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
                half3 normalAligned : TEXCOORD3;
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

            static const float RBX_AXIS_BLEND_WIDTH = 0.10;

            float3 RbxNarrowAxisWeights(float3 geometricNormalAligned)
            {
                float3 absoluteNormal = saturate(abs(geometricNormalAligned));
                float dominantComponent = max(absoluteNormal.x,
                    max(absoluteNormal.y, absoluteNormal.z));
                float3 componentDelta = dominantComponent.xxx - absoluteNormal;
                float3 weights = 1.0 - smoothstep(0.0, RBX_AXIS_BLEND_WIDTH, componentDelta);
                float weightSum = max(weights.x + weights.y + weights.z, 0.0001);
                return weights / weightSum;
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
                float inverseScale = rsqrt(max(max(dot(tangentWS, tangentWS),
                    dot(bitangentWS, bitangentWS)), 0.000001));
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
                output.positionAligned = input.positionOS.xyz * RbxTextureObjectAxisScale();
                output.normalAligned = normalize(input.normalOS);
                return output;
            }

            half4 RbxTexturedFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uvScale = float2(_TextureScale / max(_TextureAspect, 0.0001),
                    _TextureScale);
                float3 geometricNormalAligned = normalize(input.normalAligned);
                float3 projectionWeights = RbxNarrowAxisWeights(geometricNormalAligned);
                float2 uvX = float2(input.positionAligned.z,
                    -sign(geometricNormalAligned.x) * input.positionAligned.y) * uvScale;
                float2 uvY = float2(input.positionAligned.x,
                    -sign(geometricNormalAligned.y) * input.positionAligned.z) * uvScale;
                float2 uvZ = float2(input.positionAligned.x,
                    sign(geometricNormalAligned.z) * input.positionAligned.y) * uvScale;
                RbxProjectionCoordinates projectionX = RbxProjectionData(uvX);
                RbxProjectionCoordinates projectionY = RbxProjectionData(uvY);
                RbxProjectionCoordinates projectionZ = RbxProjectionData(uvZ);
                float3 positionDerivativeX = ddx(input.positionWS);
                float3 positionDerivativeY = ddy(input.positionWS);
                float3 baseNormalWS = normalize(input.normalWS);
                half3 textureColor = half3(0.0h, 0.0h, 0.0h);
                half roughness = 0.0h;
                float3 mappedNormalWS = float3(0.0, 0.0, 0.0);
                half metallic = 0.0h;
                half occlusion = 0.0h;

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
                half3 albedo = textureColor * _MaterialColor.rgb * partModulation;
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
