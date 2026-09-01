Shader "CoreAI/Rbx/Procedural Surface"
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
        [HideInInspector] _BaseMap("Base Map", 2D) = "white" {}
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
            #pragma vertex RbxSurfaceVertex
            #pragma fragment RbxSurfaceFragment
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

            Varyings RbxSurfaceVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionAligned = input.positionOS.xyz * RbxObjectAxisScale();
                output.normalAligned = normalize(input.normalOS);
                return output;
            }

            half4 RbxSurfaceFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                int materialMode = (int)round(_MaterialMode);
                float3 geometricNormalWS = normalize(input.normalWS);
                float3 geometricNormalAligned = normalize(input.normalAligned);
                bool objectAlignedProjection = RbxUsesObjectAlignedProjection(materialMode);
                float3 patternPosition = objectAlignedProjection
                    ? input.positionAligned
                    : input.positionWS;
                float3 geometricPatternNormal = objectAlignedProjection
                    ? geometricNormalAligned
                    : geometricNormalWS;
                half3 baseColor = RbxComposeMaterialColor(_Color.rgb, _MaterialColor.rgb,
                    _PartColorInfluence);
                RbxSurfaceSample procedural = RbxEvaluateSurface(patternPosition,
                    geometricPatternNormal, materialMode, _PatternScale, baseColor);
                float3 normalWS = RbxPerturbNormal(input.positionWS, geometricNormalWS,
                    procedural.heightGradient, objectAlignedProjection, _PatternScale,
                    _BumpStrength, procedural.height);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = procedural.albedo;
                surfaceData.specular = half3(0.04h, 0.04h, 0.04h);
                surfaceData.metallic = procedural.metallic;
                surfaceData.smoothness = procedural.smoothness;
                surfaceData.normalTS = half3(0.0h, 0.0h, 1.0h);
                surfaceData.emission = half3(0.0h, 0.0h, 0.0h);
                surfaceData.occlusion = procedural.occlusion;
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

    Fallback Off
}
