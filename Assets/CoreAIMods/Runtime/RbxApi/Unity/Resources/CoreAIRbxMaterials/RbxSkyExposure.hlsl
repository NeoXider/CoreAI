#ifndef RBX_SKY_EXPOSURE_INCLUDED
#define RBX_SKY_EXPOSURE_INCLUDED

// WHY one shared term: a cave must be dark while the hill above it is lit in the same frame, so the sky's
// share of the light (ambient and environment reflections) is scaled per surface by how much of the sky
// it sees, 1 in the open and 0 deep in rock. The sun (its shadows already stop it), point and spot lights
// and emission are left alone. Terrain carries the exposure per vertex in the colour's alpha, selected by
// _RbxSkyExposureSource = 1 on its material copies; a Part carries one value in _RbxSkyExposure through
// its property block. Both are material floats, so no shader variant is added. Include after URP's
// Lighting.hlsl and declare both floats in the pass's UnityPerMaterial buffer.

// WHY a global: it is Roblox's Lighting.Ambient, the light of areas the sky does not reach, set by the
// lighting sink; black until one does.
half4 _RbxOccludedAmbient;

half RbxVertexSkyExposure(half4 vertexColor, float exposureSource, float exposure)
{
    return exposureSource > 0.5 ? vertexColor.a : (half)saturate(exposure);
}

// WHY through occlusion: UniversalFragmentPBR multiplies its ambient and its reflections, and nothing
// else, by surfaceData.occlusion. What the sky no longer gives is replaced by the occluded ambient on the
// diffuse part, added as emission so URP's own terms stay as they are.
void RbxApplySkyExposure(half exposure, inout SurfaceData surfaceData)
{
    half visible = saturate(exposure);
    half3 diffuse = surfaceData.albedo * OneMinusReflectivityMetallic(surfaceData.metallic);
    surfaceData.emission += _RbxOccludedAmbient.rgb * diffuse * surfaceData.occlusion * (1.0h - visible);
    surfaceData.occlusion *= visible;
}

half3 RbxExposedAmbient(half exposure, half3 skyAmbient)
{
    return lerp(_RbxOccludedAmbient.rgb, skyAmbient, saturate(exposure));
}

#endif
