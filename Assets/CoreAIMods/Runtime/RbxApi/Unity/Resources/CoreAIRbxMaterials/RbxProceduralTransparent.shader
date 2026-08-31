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
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "RbxProceduralCommon.hlsl"

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
                output.positionWS = positionInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            float RbxTransparentHeight(float3 positionWS, int materialMode)
            {
                if (materialMode == 1)
                {
                    return RbxFbm(positionWS * 2.4) * 0.18;
                }

                float3 position = positionWS * 1.4;
                float fractureA = pow(saturate(1.0 - abs(sin(position.x * 2.7 + position.z * 1.2 +
                    RbxFbm(position * 0.7) * 4.0))), 7.0);
                float fractureB = pow(saturate(1.0 - abs(sin(position.z * 3.1 - position.y * 1.7 +
                    RbxValueNoise(position * 1.8) * 3.0))), 9.0);
                return RbxFbm(position * 1.6) * 0.24 + saturate(fractureA + fractureB) * 0.76;
            }

            float3 RbxTransparentNormal(float3 positionWS, float3 normalWS, int materialMode)
            {
                float3 referenceAxis = abs(normalWS.y) < 0.92
                    ? float3(0.0, 1.0, 0.0)
                    : float3(1.0, 0.0, 0.0);
                float3 tangentWS = normalize(cross(referenceAxis, normalWS));
                float3 bitangentWS = normalize(cross(normalWS, tangentWS));
                float epsilon = 0.018;
                float centerHeight = RbxTransparentHeight(positionWS, materialMode);
                float tangentSlope = (RbxTransparentHeight(positionWS + tangentWS * epsilon,
                    materialMode) - centerHeight) * _BumpStrength / epsilon;
                float bitangentSlope = (RbxTransparentHeight(positionWS + bitangentWS * epsilon,
                    materialMode) - centerHeight) * _BumpStrength / epsilon;
                return normalize(normalWS - tangentWS * tangentSlope - bitangentWS * bitangentSlope);
            }

            half4 RbxTransparentFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                int materialMode = (int)round(_MaterialMode);
                float3 normalWS = normalize(input.normalWS);
                half3 viewDirectionWS = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half fresnel = pow(1.0h - saturate(dot(normalWS, viewDirectionWS)), 3.0h);
                half3 materialColor = RbxComposeMaterialColor(_Color.rgb, _MaterialColor.rgb,
                    _PartColorInfluence);
                half materialAlpha = saturate(_Color.a * _MaterialColor.a);

                if (materialMode == 0)
                {
                    float3 animatedPosition = input.positionWS * _PatternScale +
                        float3(_Time.y * 0.18, -_Time.y * 0.34, _Time.y * 0.12);
                    float energyNoise = RbxFbm(animatedPosition * 1.6);
                    float2 energyUv = RbxProjectedUv(animatedPosition, normalWS);
                    float diagonalA = abs(sin((energyUv.x + energyUv.y) * 7.0 + _Time.y * 2.4));
                    float diagonalB = abs(sin((energyUv.x - energyUv.y) * 6.0 - _Time.y * 1.7));
                    float lattice = smoothstep(0.78, 0.98, max(diagonalA, diagonalB));
                    float scan = 0.5 + 0.5 * sin(dot(animatedPosition, float3(1.4, 2.1, 0.9)) * 3.0);
                    half alpha = materialAlpha *
                        (0.08h + fresnel * 0.34h + lattice * 0.24h + scan * 0.08h);
                    half intensity = 1.35h + (half)energyNoise * 1.25h + lattice * 1.5h + fresnel * 1.2h;
                    return half4(materialColor * intensity, saturate(alpha));
                }

                normalWS = RbxTransparentNormal(input.positionWS, normalWS, materialMode);
                fresnel = pow(1.0h - saturate(dot(normalWS, viewDirectionWS)), 3.0h);
                float height = RbxTransparentHeight(input.positionWS, materialMode);

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

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = viewDirectionWS;
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = ComputeFogFactor(input.positionCS.z);
                inputData.vertexLighting = VertexLighting(input.positionWS, normalWS);
                inputData.bakedGI = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = saturate(surfaceData.alpha);
                return color;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
