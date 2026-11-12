#ifndef RBX_RANGE_LIGHTS_INCLUDED
#define RBX_RANGE_LIGHTS_INCLUDED

// WHY: a Roblox PointLight or SpotLight lights what lies within its Range, strongest next to it and fading to nothing
// at the Range. URP's inverse square leaves all but the first metre or two of a long Range dark: a Block World torch
// of Range 26 studs lit one block. RbxFragmentPBR is URP 17's UniversalFragmentPBR with each point or spot light's
// distance term replaced by RbxRangeAttenuation; the main light, ambient, reflections, shadows, cones and light layers
// are URP's own. Include after URP's Lighting.hlsl. It adds no keyword, so no shader variant.

// WHY a softened inverse square inside URP's own range window: the blocks next to a light stay the brightest, the
// light falls by no more than 1 / (1 + 2 (d / Range)^2) before the window takes it to nothing at the Range, and it
// never exceeds the light's colour times its intensity, so the block a torch stands on is not burnt out. Over a
// Range: 1 next to the light, 0.8 at a third, 0.34 at two thirds, 0.05 at nine tenths.
#define RBX_RANGE_LIGHT_SOFTENING 2.0

// WHY a factor on URP's term: that term is its range window over distanceSqr, times the cone, so multiplying by
// distanceSqr over the softening keeps the window and the cone and trades only the inverse square.
float RbxRangeLightScale(float distanceSqr, float invRangeSqr)
{
    return distanceSqr / (1.0 + RBX_RANGE_LIGHT_SOFTENING * saturate(distanceSqr * invRangeSqr));
}

// WHY only a point or spot light (w = 1): a directional additional light has no range and keeps URP's term.
Light RbxGetAdditionalLight(uint i, InputData inputData, half4 shadowMask, AmbientOcclusionFactor aoFactor)
{
    Light light = GetAdditionalLight(i, inputData, shadowMask, aoFactor);
#if USE_CLUSTER_LIGHT_LOOP
    int lightIndex = i;
#else
    int lightIndex = GetPerObjectLightIndex(i);
#endif
#if USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA
    float4 lightPositionWS = _AdditionalLightsBuffer[lightIndex].position;
    float invRangeSqr = _AdditionalLightsBuffer[lightIndex].attenuation.x;
#else
    float4 lightPositionWS = _AdditionalLightsPosition[lightIndex];
    float invRangeSqr = _AdditionalLightsAttenuation[lightIndex].x;
#endif
    if (lightPositionWS.w > 0.5)
    {
        float3 lightVector = lightPositionWS.xyz - inputData.positionWS;
        float distanceSqr = max(dot(lightVector, lightVector), HALF_MIN);
        light.distanceAttenuation *= RbxRangeLightScale(distanceSqr, invRangeSqr);
    }

    return light;
}

half4 RbxFragmentPBR(InputData inputData, SurfaceData surfaceData)
{
    #if defined(_SPECULARHIGHLIGHTS_OFF)
    bool specularHighlightsOff = true;
    #else
    bool specularHighlightsOff = false;
    #endif
    BRDFData brdfData;
    InitializeBRDFData(surfaceData, brdfData);

    #if defined(DEBUG_DISPLAY)
    half4 debugColor;
    if (CanDebugOverrideOutputColor(inputData, surfaceData, brdfData, debugColor))
    {
        return debugColor;
    }
    #endif

    BRDFData brdfDataClearCoat = CreateClearCoatBRDFData(surfaceData, brdfData);
    half4 shadowMask = CalculateShadowMask(inputData);
    AmbientOcclusionFactor aoFactor = CreateAmbientOcclusionFactor(inputData, surfaceData);
    uint meshRenderingLayers = GetMeshRenderingLayer();
    Light mainLight = GetMainLight(inputData, shadowMask, aoFactor);
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI);

    LightingData lightingData = CreateLightingData(inputData, surfaceData);
    lightingData.giColor = GlobalIllumination(brdfData, brdfDataClearCoat, surfaceData.clearCoatMask,
                                              inputData.bakedGI, aoFactor.indirectAmbientOcclusion, inputData.positionWS,
                                              inputData.normalWS, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);
#ifdef _LIGHT_LAYERS
    if (IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers))
#endif
    {
        lightingData.mainLightColor = LightingPhysicallyBased(brdfData, brdfDataClearCoat, mainLight,
                                                              inputData.normalWS, inputData.viewDirectionWS,
                                                              surfaceData.clearCoatMask, specularHighlightsOff);
    }

    #if defined(_ADDITIONAL_LIGHTS)
    uint pixelLightCount = GetAdditionalLightsCount();

    #if USE_CLUSTER_LIGHT_LOOP
    [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
    {
        CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK

        Light light = RbxGetAdditionalLight(lightIndex, inputData, shadowMask, aoFactor);
#ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
        {
            lightingData.additionalLightsColor += LightingPhysicallyBased(brdfData, brdfDataClearCoat, light,
                                                                          inputData.normalWS, inputData.viewDirectionWS,
                                                                          surfaceData.clearCoatMask, specularHighlightsOff);
        }
    }
    #endif

    LIGHT_LOOP_BEGIN(pixelLightCount)
        Light light = RbxGetAdditionalLight(lightIndex, inputData, shadowMask, aoFactor);
#ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
        {
            lightingData.additionalLightsColor += LightingPhysicallyBased(brdfData, brdfDataClearCoat, light,
                                                                          inputData.normalWS, inputData.viewDirectionWS,
                                                                          surfaceData.clearCoatMask, specularHighlightsOff);
        }
    LIGHT_LOOP_END
    #endif

    #if defined(_ADDITIONAL_LIGHTS_VERTEX)
    lightingData.vertexLightingColor += inputData.vertexLighting * brdfData.diffuse;
    #endif

#if REAL_IS_HALF
    return min(CalculateFinalColor(lightingData, surfaceData.alpha), HALF_MAX);
#else
    return CalculateFinalColor(lightingData, surfaceData.alpha);
#endif
}

#endif
