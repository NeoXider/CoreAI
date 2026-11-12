Shader "CoreAI/Rbx/Procedural Transparent"
{
    Properties
    {
        [MainColor] _BaseColor("Part Color", Color) = (1,1,1,1)
        [HideInInspector] _Color("Part Color Compatibility", Color) = (1,1,1,1)
        [HideInInspector] _MaterialColor("Intrinsic Material Color", Color) = (1,1,1,1)
        [HideInInspector] _PartColorInfluence("Part Color Influence", Range(0,1)) = 0.5
        [HideInInspector] _MaterialMode("Material Mode", Float) = 0
        [HideInInspector] _PatternScale("Pattern Scale", Float) = 1
        [HideInInspector] _BumpStrength("Bump Strength", Float) = 0.3
        [HideInInspector] _SrcBlend("Source Blend", Float) = 5
        [HideInInspector] _DstBlend("Destination Blend", Float) = 10
        [HideInInspector] _SceneRefraction("Scene Refraction", Float) = 0
        [HideInInspector] _RbxSkyExposure("Sky Exposure", Range(0,1)) = 1
        [HideInInspector] _RbxSkyExposureSource("Sky Exposure From Vertex Colour", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "UniversalMaterialType" = "Lit"
        }
        LOD 220

        Pass
        {
            Name "ForwardTransparent"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Blend [_SrcBlend] [_DstBlend]
            Cull Back
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex RbxTransparentVertex
            #pragma fragment RbxTransparentFragment
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
            // WHY: a global keyword (RbxSceneTextures) because the Rbx runtime cannot read the pipeline
            // asset; off, Glass keeps the alpha-blended look, as Roblox shows it on mobile.
            #pragma multi_compile_fragment _ _RBX_SCENE_TEXTURES

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #if defined(_RBX_SCENE_TEXTURES)
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #endif
            #include "RbxProceduralCommon.hlsl"
            #include "RbxSkyExposure.hlsl"
            #include "RbxRangeLights.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _Color;
                half4 _MaterialColor;
                float _PartColorInfluence;
                float _MaterialMode;
                float _PatternScale;
                float _BumpStrength;
                float _SrcBlend;
                float _DstBlend;
                float _SceneRefraction;
                float _RbxSkyExposure;
                float _RbxSkyExposureSource;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float fogDepth : TEXCOORD2;
                half skyExposure : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings RbxTransparentVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positionInputs.positionCS;
                // WHY the view depth and not a fog factor: the depth is affine in the vertex position, so
                // it interpolates exactly across a part of any size, and the fragment clamps it the way
                // URP's own fragment fog does. URP's ComputeFogFactor fed the fragment-stage
                // SV_POSITION.z (device depth) put every surface near the far plane, and a factor
                // computed per vertex clamps the vertices behind the camera, which spread the far fog of
                // a large part (the baseplate) onto the ground in front of the camera.
                output.fogDepth = -positionInputs.positionVS.z;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.skyExposure = RbxVertexSkyExposure(input.color, _RbxSkyExposureSource, _RbxSkyExposure);
                return output;
            }

            // WHY one return: with an early return inside the branch, FXC reported "use of potentially
            // uninitialized variable (RbxTransparentHeight)" for the D3D fragment program.
            float RbxTransparentHeight(float3 positionWS, int materialMode)
            {
                float height;
                if (materialMode == 1)
                {
                    height = RbxFbm(positionWS * 2.4) * 0.18;
                }
                else
                {
                    float3 position = positionWS * 1.4;
                    float fractureA = pow(saturate(1.0 - abs(sin(position.x * 2.7 + position.z * 1.2 +
                        RbxFbm(position * 0.7) * 4.0))), 7.0);
                    float fractureB = pow(saturate(1.0 - abs(sin(position.z * 3.1 - position.y * 1.7 +
                        RbxValueNoise(position * 1.8) * 3.0))), 9.0);
                    height = RbxFbm(position * 1.6) * 0.24 + saturate(fractureA + fractureB) * 0.76;
                }

                return height;
            }

            float3 RbxTransparentNormal(float3 patternPosition, float3 normalWS, int materialMode,
                float patternScale)
            {
                float3 referenceAxis = abs(normalWS.y) < 0.92
                    ? float3(0.0, 1.0, 0.0)
                    : float3(1.0, 0.0, 0.0);
                float3 tangentWS = normalize(cross(referenceAxis, normalWS));
                float3 bitangentWS = normalize(cross(normalWS, tangentWS));
                float epsilon = 0.018;
                float patternEpsilon = epsilon * max(patternScale, 0.0001);
                float centerHeight = RbxTransparentHeight(patternPosition, materialMode);
                float tangentSlope = (RbxTransparentHeight(
                    patternPosition + tangentWS * patternEpsilon,
                    materialMode) - centerHeight) * _BumpStrength / epsilon;
                float bitangentSlope = (RbxTransparentHeight(
                    patternPosition + bitangentWS * patternEpsilon,
                    materialMode) - centerHeight) * _BumpStrength / epsilon;
                return normalize(normalWS - tangentWS * tangentSlope - bitangentWS * bitangentSlope);
            }

            float RbxForceFieldLattice(float2 uv)
            {
                float diagonalA = abs(sin((uv.x + uv.y) * 7.0 + _Time.y * 2.4));
                float diagonalB = abs(sin((uv.x - uv.y) * 6.0 - _Time.y * 1.7));
                return smoothstep(0.78, 0.98, max(diagonalA, diagonalB));
            }

            float RbxBlendedForceFieldLattice(float3 position, float3 weights)
            {
                float2 uvX;
                float2 uvY;
                float2 uvZ;
                RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
                float lattice = 0.0;
                UNITY_BRANCH if (weights.x > 0.0)
                {
                    lattice += RbxForceFieldLattice(uvX) * weights.x;
                }
                UNITY_BRANCH if (weights.y > 0.0)
                {
                    lattice += RbxForceFieldLattice(uvY) * weights.y;
                }
                UNITY_BRANCH if (weights.z > 0.0)
                {
                    lattice += RbxForceFieldLattice(uvZ) * weights.z;
                }
                return lattice;
            }

            half4 RbxTransparentFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                int materialMode = (int)round(_MaterialMode);
                float3 geometricNormalWS = normalize(input.normalWS);
                float3 normalWS = geometricNormalWS;
                half3 viewDirectionWS = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half fresnel = pow(1.0h - saturate(dot(geometricNormalWS, viewDirectionWS)), 3.0h);
                half3 materialColor = RbxComposeMaterialColor(_Color.rgb, _MaterialColor.rgb,
                    _PartColorInfluence);
                half materialAlpha = saturate(_Color.a * _MaterialColor.a);

                if (materialMode == 0)
                {
                    float3 animatedPosition = input.positionWS * _PatternScale +
                        float3(_Time.y * 0.18, -_Time.y * 0.34, _Time.y * 0.12);
                    float energyNoise = RbxFbm(animatedPosition * 1.6);
                    float3 projectionWeights = RbxNarrowAxisWeights(geometricNormalWS);
                    float lattice = RbxBlendedForceFieldLattice(animatedPosition,
                        projectionWeights);
                    float scan = 0.5 + 0.5 * sin(dot(animatedPosition, float3(1.4, 2.1, 0.9)) * 3.0);
                    half alpha = materialAlpha *
                        (0.08h + fresnel * 0.34h + lattice * 0.24h + scan * 0.08h);
                    half intensity = 1.35h + (half)energyNoise * 1.25h + lattice * 1.5h + fresnel * 1.2h;
                    return half4(materialColor * intensity, saturate(alpha));
                }

                float3 patternPosition = input.positionWS * _PatternScale;
                normalWS = RbxTransparentNormal(patternPosition, normalWS, materialMode,
                    _PatternScale);
                fresnel = pow(1.0h - saturate(dot(normalWS, viewDirectionWS)), 3.0h);
                float height = RbxTransparentHeight(patternPosition, materialMode);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.specular = half3(0.04h, 0.04h, 0.04h);
                surfaceData.metallic = 0.0h;
                surfaceData.normalTS = half3(0.0h, 0.0h, 1.0h);
                surfaceData.occlusion = 1.0h;

                if (materialMode == 1)
                {
                    surfaceData.albedo = materialColor * (0.12h + (half)height * 0.08h);
                    surfaceData.smoothness = 0.97h;
                    surfaceData.emission = materialColor * (0.025h + fresnel * 0.18h);
                    surfaceData.alpha = materialAlpha * (0.15h + fresnel * 0.34h);
                }
                else
                {
                    half fracture = smoothstep(0.58h, 0.82h, (half)height);
                    surfaceData.albedo = materialColor * (0.42h + (half)height * 0.32h);
                    surfaceData.smoothness = lerp(0.91h, 0.62h, fracture);
                    surfaceData.emission = materialColor * (0.025h + fracture * 0.14h);
                    surfaceData.alpha = materialAlpha *
                        (0.64h + fracture * 0.22h + fresnel * 0.1h);
                }

                // WHY the emission too: its Fresnel term stands in for the sky this pane reflects.
                surfaceData.emission *= (half)saturate(input.skyExposure);
                RbxApplySkyExposure(input.skyExposure, surfaceData);

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = viewDirectionWS;
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = ComputeFogFactorZ0ToFar(max(input.fogDepth - _ProjectionParams.y, 0.0));
                inputData.vertexLighting = VertexLighting(input.positionWS, normalWS);
                inputData.bakedGI = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);

                half4 color = RbxFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = saturate(surfaceData.alpha);
                #if defined(_RBX_SCENE_TEXTURES)
                    // WHY: alpha blending alone read as tinted plastic. Glass shows the scene behind it
                    // bent by its surface normal and tinted, with the lit reflection on top; Part
                    // transparency fades the whole pane back to the unbent scene. Air (also mode 1)
                    // keeps _SceneRefraction at 0 and stays invisible.
                    UNITY_BRANCH if (materialMode == 1 && _SceneRefraction > 0.5)
                    {
                        float2 screenUV = inputData.normalizedScreenSpaceUV;
                        float3 viewNormal = TransformWorldToViewDir(normalWS);
                        half3 straight = SampleSceneColor(screenUV);
                        half3 refracted = SampleSceneColor(screenUV + viewNormal.xy * 0.03);
                        half3 glassTint = lerp(half3(1.0h, 1.0h, 1.0h), materialColor, 0.35h);
                        half3 throughGlass = refracted * glassTint * (1.0h - fresnel) + color.rgb;
                        color.rgb = lerp(straight, throughGlass, saturate(_Color.a));
                        color.a = 1.0h;
                    }
                #endif
                return color;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
