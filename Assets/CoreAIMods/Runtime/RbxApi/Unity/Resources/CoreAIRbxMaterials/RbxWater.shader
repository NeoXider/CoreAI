Shader "CoreAI/Rbx/Water"
{
    Properties
    {
        [MainColor] _BaseColor("Part Color", Color) = (1,1,1,1)
        [HideInInspector] _Color("Part Color Compatibility", Color) = (1,1,1,1)
        [HideInInspector] _MaterialColor("Intrinsic Material Color", Color) = (0.08,0.42,0.72,0.62)
        [HideInInspector] _PartColorInfluence("Part Color Influence", Range(0,1)) = 0.22
        [HideInInspector] _MaterialMode("Material Mode", Float) = 2
        _PatternScale("Wave Scale (cycles per metre)", Float) = 0.7
        _BumpStrength("Wave Strength", Float) = 0.2
        _WaveSpeed("Wave Speed", Float) = 0.6
        _Smoothness("Smoothness", Range(0,1)) = 0.94
        _Absorption("Absorption Per Metre", Float) = 0.6
        _Refraction("Refraction Strength", Range(0,0.2)) = 0.035
        _ShoreFade("Shore Fade (metres)", Float) = 0.2
        _FallbackOpacity("Opacity Without Scene Textures", Range(0,1)) = 0.7
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
            "IgnoreProjector" = "True"
        }
        LOD 220

        Pass
        {
            Name "ForwardWater"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Blend SrcAlpha OneMinusSrcAlpha
            // WHY both sides: a swimmer under water looks up at the surface, the face's back.
            Cull Off
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex RbxWaterVertex
            #pragma fragment RbxWaterFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            // WHY: URP 17 renders Forward+ (PC_Renderer m_RenderingMode: 2) and delivers the main
            // light through the clustered light loop. Without this keyword the pass compiles the
            // non-clustered variant and the water gets no sun glint and no shadows.
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            // WHY: a global keyword (RbxSceneTextures) because the Rbx runtime cannot read the pipeline
            // asset; off, the water falls back to an alpha-blended surface with the same waves.
            #pragma multi_compile_fragment _ _RBX_SCENE_TEXTURES

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #if defined(_RBX_SCENE_TEXTURES)
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #endif
            #include "RbxProceduralCommon.hlsl"
            #include "RbxSkyExposure.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _Color;
                half4 _MaterialColor;
                float _PartColorInfluence;
                float _MaterialMode;
                float _PatternScale;
                float _BumpStrength;
                float _WaveSpeed;
                float _Smoothness;
                float _Absorption;
                float _Refraction;
                float _ShoreFade;
                float _FallbackOpacity;
                float _RbxSkyExposure;
                float _RbxSkyExposureSource;
            CBUFFER_END

            // WHY: globals set by RbxWaterTime; both stay zero, so the waves follow _Time, until a
            // capture pins the clock.
            float _RbxWaterTimeOverride;
            float _RbxWaterTimeOverrideEnabled;

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

            Varyings RbxWaterVertex(Attributes input)
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

            float RbxWaterTime()
            {
                return _RbxWaterTimeOverrideEnabled > 0.5 ? _RbxWaterTimeOverride : _Time.y;
            }

            // WHY: two drifting simplex layers (a long swell and a finer chop moving another way) give
            // waves that never visibly repeat, with analytical slopes, so no normal texture is needed.
            // Each layer fades out once a pixel covers most of its wavelength, which keeps distant water
            // calm instead of sparkling.
            float3 RbxWaterWaveSlope(float3 positionWS, float time)
            {
                float3 position = positionWS * _PatternScale;
                float drift = time * _WaveSpeed;
                float footprint = RbxPixelFootprint(position);
                float swellVisibility = RbxSimplexOctaveVisibility(footprint);
                float chopVisibility = RbxSimplexOctaveVisibility(footprint * 2.3);
                float4 swell = RbxSimplexNoise01Grad(float3(position.x + drift * 0.55,
                    position.z - drift * 0.3, drift * 0.2));
                float4 chop = RbxSimplexNoise01Grad(float3(position.x * 2.3 - drift * 0.4,
                    position.z * 2.3 + drift * 0.75, drift * 0.37 + 11.0));
                float2 slope = swell.xy * (_PatternScale * swellVisibility)
                    + chop.xy * (_PatternScale * 2.3 * 0.45 * chopVisibility);
                return float3(slope.x, 0.0, slope.y);
            }

            half4 RbxWaterFragment(Varyings input, FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 geometricNormalWS = normalize(input.normalWS);
                float3 slope = RbxWaterWaveSlope(input.positionWS, RbxWaterTime());
                float3 tangentialSlope = slope - dot(slope, geometricNormalWS) * geometricNormalWS;
                float3 normalWS = normalize(geometricNormalWS - tangentialSlope * _BumpStrength);
                half3 viewDirectionWS = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half fresnel = 0.02h + 0.98h * (half)pow(1.0 - saturate(dot(normalWS, viewDirectionWS)), 5.0);
                half3 waterColor = RbxComposeMaterialColor(_Color.rgb, _MaterialColor.rgb,
                    _PartColorInfluence);
                half partOpacity = saturate(_Color.a);
                half skyExposure = saturate(input.skyExposure);
                // WHY: the light the water body scatters back is the sky's in the open; under rock only the
                // occluded ambient is left to scatter.
                half scatterLight = lerp(saturate(Luminance(_RbxOccludedAmbient.rgb)), 1.0h, skyExposure);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = waterColor * 0.06h;
                surfaceData.specular = half3(0.02h, 0.02h, 0.02h);
                surfaceData.metallic = 0.0h;
                surfaceData.smoothness = (half)saturate(_Smoothness);
                surfaceData.normalTS = half3(0.0h, 0.0h, 1.0h);
                surfaceData.emission = half3(0.0h, 0.0h, 0.0h);
                // WHY zero: URP's own indirect light is replaced below, so it must not be added twice.
                surfaceData.occlusion = 0.0h;
                surfaceData.alpha = 1.0h;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = viewDirectionWS;
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = ComputeFogFactorZ0ToFar(max(input.fogDepth - _ProjectionParams.y, 0.0));
                inputData.vertexLighting = VertexLighting(input.positionWS, normalWS);
                inputData.bakedGI = RbxExposedAmbient(skyExposure, SampleSH(normalWS));
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);

                // WHY: this is only the reflected part (sky, probes, sun glint); the environment term
                // carries its own Fresnel weight, so it is added, not blended.
                half3 surface = UniversalFragmentPBR(inputData, surfaceData).rgb;
                BRDFData brdfData;
                InitializeBRDFData(surfaceData, brdfData);
                // WHY: a wave tilted towards a low camera reflects a ray that points below the horizon, and
                // the sky cubemap answers it with the procedural sky's ground colour: a sea seen from a
                // grazing angle turned into brown blotches ringed by the white horizon. Such a ray would hit
                // the next wave, which mirrors the sky at a grazing angle, so it is lifted to the horizon.
                // The Fresnel weight still uses the wave normal.
                float3 reflectVector = reflect(-viewDirectionWS, normalWS);
                reflectVector = SafeNormalize(reflectVector
                    - min(dot(reflectVector, geometricNormalWS), 0.0) * geometricNormalWS);
                half3 environment = GlossyEnvironmentReflection((half3)reflectVector, input.positionWS,
                    brdfData.perceptualRoughness, 1.0h, inputData.normalizedScreenSpaceUV) * skyExposure;
                half environmentFresnel = Pow4(1.0h - saturate(dot(normalWS, (float3)viewDirectionWS)));
                surface += EnvironmentBRDF(brdfData, inputData.bakedGI, environment, environmentFresnel);
                half3 color;
                half alpha;

                #if defined(_RBX_SCENE_TEXTURES)
                    float2 screenUV = inputData.normalizedScreenSpaceUV;
                    float surfaceDepth = -TransformWorldToView(input.positionWS).z;
                    float straightDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                    float straightThickness = max(straightDepth - surfaceDepth, 0.0);
                    float3 bend = TransformWorldToViewDir(normalWS - geometricNormalWS);
                    float2 refractedUV = screenUV + bend.xy * (_Refraction * saturate(straightThickness));
                    float refractedDepth = LinearEyeDepth(SampleSceneDepth(refractedUV), _ZBufferParams);
                    // WHY: an offset that lands on something in front of the water would pull that object
                    // into the water, so such pixels keep the straight sample.
                    refractedUV = refractedDepth > surfaceDepth ? refractedUV : screenUV;
                    float thickness = refractedDepth > surfaceDepth
                        ? refractedDepth - surfaceDepth
                        : straightThickness;
                    half3 sceneColor = SampleSceneColor(refractedUV);
                    // WHY: clear water absorbs the colours it does not show (red first for blue water),
                    // so deep water takes the water colour and shallow water shows the bottom.
                    half3 transmittance = (half3)exp(-thickness * _Absorption
                        * (1.0 - (float3)saturate(waterColor)));
                    half3 transmitted = sceneColor * transmittance
                        + waterColor * (0.3h * scatterLight) * (1.0h - transmittance);
                    color = transmitted * (1.0h - fresnel) + surface;
                    alpha = (half)saturate(straightThickness / max(_ShoreFade, 0.0001)) * partOpacity;
                #else
                    color = waterColor * (0.3h * scatterLight) * (1.0h - fresnel) + surface;
                    alpha = saturate(lerp((half)_FallbackOpacity, 1.0h, fresnel)) * partOpacity;
                #endif

                // WHY a tinted underside: seen from below, the surface mirrored the sky above it, a pale sheet over the
                // swimmer; water seen from inside is its own colour, as dark as the light it scatters.
                if (!IS_FRONT_VFACE(facing, true, false))
                {
                    color = waterColor * (0.35h * scatterLight);
                    alpha = 0.85h * partOpacity;
                }

                color = MixFog(color, inputData.fogCoord);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    Fallback "CoreAI/Rbx/Procedural Transparent"
}
