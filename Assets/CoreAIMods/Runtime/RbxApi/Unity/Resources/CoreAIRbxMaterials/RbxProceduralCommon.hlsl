#ifndef COREAI_RBX_PROCEDURAL_COMMON_INCLUDED
#define COREAI_RBX_PROCEDURAL_COMMON_INCLUDED

#include "RbxNoiseShader.hlsl"

struct RbxSurfaceSample
{
    half3 albedo;
    half metallic;
    half smoothness;
    half occlusion;
    float height;
    float3 heightGradient;
};

float RbxHash31(float3 value)
{
    value = frac(value * 0.1031);
    value += dot(value, value.yzx + 33.33);
    return frac((value.x + value.y) * value.z);
}

float RbxValueNoise(float3 position)
{
    float3 cell = floor(position);
    float3 localPosition = frac(position);
    float3 blend = localPosition * localPosition * (3.0 - 2.0 * localPosition);
    float x00 = lerp(RbxHash31(cell), RbxHash31(cell + float3(1.0, 0.0, 0.0)), blend.x);
    float x10 = lerp(RbxHash31(cell + float3(0.0, 1.0, 0.0)),
        RbxHash31(cell + float3(1.0, 1.0, 0.0)), blend.x);
    float x01 = lerp(RbxHash31(cell + float3(0.0, 0.0, 1.0)),
        RbxHash31(cell + float3(1.0, 0.0, 1.0)), blend.x);
    float x11 = lerp(RbxHash31(cell + float3(0.0, 1.0, 1.0)),
        RbxHash31(cell + float3(1.0, 1.0, 1.0)), blend.x);
    return lerp(lerp(x00, x10, blend.y), lerp(x01, x11, blend.y), blend.z);
}

float RbxFbm(float3 position)
{
    float value = RbxValueNoise(position) * 0.5333;
    position = position * 2.03 + 17.17;
    value += RbxValueNoise(position) * 0.2667;
    position = position * 2.01 + 9.23;
    value += RbxValueNoise(position) * 0.1333;
    position = position * 2.04 + 5.71;
    value += RbxValueNoise(position) * 0.0667;
    return value;
}

float3 RbxObjectAxisScale()
{
    float3x3 objectToWorld = (float3x3)GetObjectToWorldMatrix();
    return float3(length(mul(objectToWorld, float3(1.0, 0.0, 0.0))),
        length(mul(objectToWorld, float3(0.0, 1.0, 0.0))),
        length(mul(objectToWorld, float3(0.0, 0.0, 1.0))));
}

bool RbxUsesObjectAlignedProjection(int materialMode)
{
    return materialMode == 3 || materialMode == 5 || materialMode == 8 ||
        materialMode == 10 || materialMode == 11 || materialMode == 12 ||
        materialMode == 13 || materialMode == 14 || materialMode == 17;
}

static const float RBX_AXIS_BLEND_WIDTH = 0.10;

float3 RbxNarrowAxisWeights(float3 geometricNormal)
{
    float3 absoluteNormal = saturate(abs(geometricNormal));
    float dominantComponent = max(absoluteNormal.x, max(absoluteNormal.y, absoluteNormal.z));
    float3 componentDelta = dominantComponent.xxx - absoluteNormal;
    float3 weights = 1.0 - smoothstep(0.0, RBX_AXIS_BLEND_WIDTH, componentDelta);
    float weightSum = max(weights.x + weights.y + weights.z, 0.0001);
    return weights / weightSum;
}

void RbxPatternProjectionUvs(float3 position, out float2 uvX, out float2 uvY, out float2 uvZ)
{
    // WHY: U follows a row or plank horizontally (X on top/front, Z on a side), while V uses Y on walls and Z on top; every axis remains in world-size units.
    uvX = position.zy;
    uvY = position.xz;
    uvZ = position.xy;
}

float RbxCellEdge(float2 cell)
{
    return min(min(cell.x, 1.0 - cell.x), min(cell.y, 1.0 - cell.y));
}

void RbxWoodPlankPattern(float2 uv, float seed, out float seam, out float grain,
    out float plankTone)
{
    float row = floor(uv.y * 1.35);
    float stagger = fmod(abs(row), 2.0) * 0.5;
    float cellX = floor(uv.x * 0.42 + stagger);
    float2 plankCell = frac(float2(uv.x * 0.42 + stagger, uv.y * 1.35));
    seam = 1.0 - smoothstep(0.018, 0.055, RbxCellEdge(plankCell));
    float grainNoise = RbxValueNoise(float3(uv * 0.8, seed));
    grain = 0.5 + 0.5 * sin(uv.x * 22.0 + grainNoise * 6.0);
    plankTone = 0.76 + RbxHash31(float3(cellX, row, 4.0 + seed)) * 0.35;
}

void RbxBrickPattern(float2 uv, float seed, out float mortar, out float brickTone)
{
    float row = floor(uv.y * 1.28);
    float stagger = fmod(abs(row), 2.0) * 0.5;
    float cellX = floor(uv.x * 0.7 + stagger);
    float2 brickCell = frac(float2(uv.x * 0.7 + stagger, uv.y * 1.28));
    mortar = 1.0 - smoothstep(0.025, 0.075, RbxCellEdge(brickCell));
    brickTone = 0.72 + RbxHash31(float3(cellX, row, 9.0 + seed)) * 0.42;
}

void RbxCobblestonePattern(float2 uv, float seed, out float stoneMask, out float dome,
    out float stoneTone)
{
    float2 grid = uv * float2(1.55, 1.35);
    float row = floor(grid.y);
    grid.x += fmod(abs(row), 2.0) * 0.5;
    float2 cell = floor(grid);
    float2 localPosition = frac(grid) - 0.5;
    float2 jitter = float2(RbxHash31(float3(cell, 11.0 + seed)),
        RbxHash31(float3(cell, 23.0 + seed))) - 0.5;
    localPosition -= jitter * 0.14;
    float width = 0.39 + RbxHash31(float3(cell, 31.0 + seed)) * 0.045;
    float height = 0.36 + RbxHash31(float3(cell, 47.0 + seed)) * 0.04;
    float stoneDistance = length(localPosition / float2(width, height));
    stoneMask = 1.0 - smoothstep(0.82, 1.0, stoneDistance);
    dome = saturate(1.0 - stoneDistance * stoneDistance) * stoneMask;
    stoneTone = 0.68 + RbxHash31(float3(cell, 59.0 + seed)) * 0.38;
}

float3 RbxGrassBladeLayer(float2 uv, float density, float seed)
{
    float2 gridPosition = uv * density;
    float2 cell = floor(gridPosition);
    float2 localPosition = frac(gridPosition) - 0.5;
    float2 jitter = float2(RbxHash31(float3(cell, seed)),
        RbxHash31(float3(cell, seed + 17.0))) - 0.5;
    localPosition -= jitter * 0.38;
    float angle = (RbxHash31(float3(cell, seed + 31.0)) - 0.5) * 1.3;
    float sine = sin(angle);
    float cosine = cos(angle);
    float2 bladePosition = float2(localPosition.x * cosine - localPosition.y * sine,
        localPosition.x * sine + localPosition.y * cosine);
    float halfLength = 0.34 + RbxHash31(float3(cell, seed + 43.0)) * 0.12;
    float bladeProgress = saturate((bladePosition.y + halfLength) / (2.0 * halfLength));
    float lengthMask = smoothstep(0.0, 0.08, bladeProgress)
        * (1.0 - smoothstep(0.84, 1.0, bladeProgress));
    float profile = sqrt(max(1.0 - abs(bladeProgress * 2.0 - 1.0), 0.0));
    float baseWidth = 0.055 + RbxHash31(float3(cell, seed + 59.0)) * 0.032;
    float lean = (RbxHash31(float3(cell, seed + 71.0)) - 0.5) * 0.24;
    float center = lean * (bladeProgress * bladeProgress - 0.18);
    float halfWidth = baseWidth * profile;
    float crossBlade = abs(bladePosition.x - center);
    float blade = (1.0 - smoothstep(halfWidth, halfWidth + 0.018, crossBlade)) * lengthMask;
    float ridge = (1.0 - smoothstep(0.006, 0.018, crossBlade)) * blade
        * smoothstep(0.12, 0.72, bladeProgress);
    float tone = RbxHash31(float3(cell, seed + 89.0));
    return float3(blade, ridge, tone);
}

void RbxGroundPattern(float2 uv, out float cracks, out float pebble,
    out float plateTone, out float pebbleTone)
{
    float2 crackedUv = uv * 1.18;
    crackedUv.x += sin(crackedUv.y * 1.73 + sin(crackedUv.y * 0.47)) * 0.13;
    crackedUv.y += sin(crackedUv.x * 1.31 + sin(crackedUv.x * 0.59)) * 0.11;
    float2 plateCell = floor(crackedUv);
    float2 platePosition = frac(crackedUv);
    float edgeDistance = RbxCellEdge(platePosition);
    cracks = 1.0 - smoothstep(0.022, 0.072, edgeDistance);

    float2 centeredPlate = platePosition - 0.5;
    float fractureDirection = RbxHash31(float3(plateCell, 113.0)) < 0.5 ? -1.0 : 1.0;
    float fractureDistance = abs(centeredPlate.x + centeredPlate.y * fractureDirection);
    float fractureLength = 1.0 - smoothstep(0.24, 0.46, abs(centeredPlate.y));
    float fracture = (1.0 - smoothstep(0.012, 0.04, fractureDistance)) * fractureLength;
    fracture *= step(0.78, RbxHash31(float3(plateCell, 127.0)));
    cracks = max(cracks, fracture);
    plateTone = RbxHash31(float3(plateCell, 139.0));

    float2 pebbleGrid = uv * 4.1;
    float2 pebbleCell = floor(pebbleGrid);
    float2 pebblePosition = frac(pebbleGrid) - 0.5;
    float2 pebbleJitter = float2(RbxHash31(float3(pebbleCell, 151.0)),
        RbxHash31(float3(pebbleCell, 163.0))) - 0.5;
    pebblePosition -= pebbleJitter * 0.48;
    float pebbleRadius = 0.075 + RbxHash31(float3(pebbleCell, 179.0)) * 0.075;
    pebble = 1.0 - smoothstep(pebbleRadius, pebbleRadius + 0.035,
        length(pebblePosition));
    pebble *= step(0.58, RbxHash31(float3(pebbleCell, 191.0)));
    pebbleTone = RbxHash31(float3(pebbleCell, 211.0));
}

void RbxBlendWoodPlankPattern(float3 position, float3 weights, out float seam, out float grain,
    out float plankTone)
{
    float2 uvX;
    float2 uvY;
    float2 uvZ;
    RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
    seam = 0.0;
    grain = 0.0;
    plankTone = 0.0;

    UNITY_BRANCH if (weights.x > 0.0)
    {
        float axisSeam;
        float axisGrain;
        float axisTone;
        RbxWoodPlankPattern(uvX, 0.0, axisSeam, axisGrain, axisTone);
        seam += axisSeam * weights.x;
        grain += axisGrain * weights.x;
        plankTone += axisTone * weights.x;
    }

    UNITY_BRANCH if (weights.y > 0.0)
    {
        float axisSeam;
        float axisGrain;
        float axisTone;
        RbxWoodPlankPattern(uvY, 7.0, axisSeam, axisGrain, axisTone);
        seam += axisSeam * weights.y;
        grain += axisGrain * weights.y;
        plankTone += axisTone * weights.y;
    }

    UNITY_BRANCH if (weights.z > 0.0)
    {
        float axisSeam;
        float axisGrain;
        float axisTone;
        RbxWoodPlankPattern(uvZ, 13.0, axisSeam, axisGrain, axisTone);
        seam += axisSeam * weights.z;
        grain += axisGrain * weights.z;
        plankTone += axisTone * weights.z;
    }
}

void RbxBlendBrickPattern(float3 position, float3 weights, out float mortar, out float brickTone)
{
    float2 uvX;
    float2 uvY;
    float2 uvZ;
    RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
    mortar = 0.0;
    brickTone = 0.0;

    UNITY_BRANCH if (weights.x > 0.0)
    {
        float axisMortar;
        float axisTone;
        RbxBrickPattern(uvX, 0.0, axisMortar, axisTone);
        mortar += axisMortar * weights.x;
        brickTone += axisTone * weights.x;
    }

    UNITY_BRANCH if (weights.y > 0.0)
    {
        float axisMortar;
        float axisTone;
        RbxBrickPattern(uvY, 7.0, axisMortar, axisTone);
        mortar += axisMortar * weights.y;
        brickTone += axisTone * weights.y;
    }

    UNITY_BRANCH if (weights.z > 0.0)
    {
        float axisMortar;
        float axisTone;
        RbxBrickPattern(uvZ, 13.0, axisMortar, axisTone);
        mortar += axisMortar * weights.z;
        brickTone += axisTone * weights.z;
    }
}

void RbxBlendCobblestonePattern(float3 position, float3 weights, out float stoneMask,
    out float stoneDome, out float stoneTone)
{
    float2 uvX;
    float2 uvY;
    float2 uvZ;
    RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
    stoneMask = 0.0;
    stoneDome = 0.0;
    stoneTone = 0.0;

    UNITY_BRANCH if (weights.x > 0.0)
    {
        float axisMask;
        float axisDome;
        float axisTone;
        RbxCobblestonePattern(uvX, 0.0, axisMask, axisDome, axisTone);
        stoneMask += axisMask * weights.x;
        stoneDome += axisDome * weights.x;
        stoneTone += axisTone * weights.x;
    }

    UNITY_BRANCH if (weights.y > 0.0)
    {
        float axisMask;
        float axisDome;
        float axisTone;
        RbxCobblestonePattern(uvY, 7.0, axisMask, axisDome, axisTone);
        stoneMask += axisMask * weights.y;
        stoneDome += axisDome * weights.y;
        stoneTone += axisTone * weights.y;
    }

    UNITY_BRANCH if (weights.z > 0.0)
    {
        float axisMask;
        float axisDome;
        float axisTone;
        RbxCobblestonePattern(uvZ, 13.0, axisMask, axisDome, axisTone);
        stoneMask += axisMask * weights.z;
        stoneDome += axisDome * weights.z;
        stoneTone += axisTone * weights.z;
    }
}

float RbxDiamondPlatePattern(float2 uv)
{
    float2 plateCell = frac(uv * 1.35) - 0.5;
    float2 diamondCoordinates = float2(plateCell.x + plateCell.y, plateCell.x - plateCell.y);
    return 1.0 - smoothstep(0.22, 0.34,
        max(abs(diamondCoordinates.x), abs(diamondCoordinates.y)));
}

float RbxSlatePattern(float2 uv, float broadNoise)
{
    return 0.5 + 0.5 * sin((uv.y + broadNoise * 0.42) * 13.0);
}

void RbxGrassPattern(float2 uv, float grassField, out float bladeMask, out float bladeRidge,
    out float bladeTone)
{
    float2 grassUv = uv + float2(grassField - 0.5, 0.5 - grassField) * 0.08;
    float3 bladeA = RbxGrassBladeLayer(grassUv, 3.2, 5.0);
    float2 rotatedUv = float2(grassUv.x * 0.819 - grassUv.y * 0.574,
        grassUv.x * 0.574 + grassUv.y * 0.819);
    float3 bladeB = RbxGrassBladeLayer(rotatedUv + float2(4.3, 7.1), 4.7, 71.0);
    bladeMask = saturate(max(bladeA.x, bladeB.x * 0.92));
    bladeRidge = saturate(max(bladeA.y, bladeB.y * 0.86));
    bladeTone = max(bladeA.z * bladeA.x, bladeB.z * bladeB.x);
}

float RbxSandPattern(float2 uv, float broadNoise)
{
    return 0.5 + 0.5 * sin(uv.x * 7.5 + sin(uv.y * 1.9) * 1.4 + broadNoise * 2.0);
}

float RbxFabricPattern(float2 uv)
{
    float warp = 0.5 + 0.5 * sin(uv.x * 42.0);
    float weft = 0.5 + 0.5 * sin(uv.y * 42.0 + 1.5708);
    return warp * 0.52 + weft * 0.48;
}

float RbxBlendDiamondPlatePattern(float3 position, float3 weights)
{
    float2 uvX;
    float2 uvY;
    float2 uvZ;
    RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
    float diamond = 0.0;
    UNITY_BRANCH if (weights.x > 0.0)
    {
        diamond += RbxDiamondPlatePattern(uvX) * weights.x;
    }
    UNITY_BRANCH if (weights.y > 0.0)
    {
        diamond += RbxDiamondPlatePattern(uvY) * weights.y;
    }
    UNITY_BRANCH if (weights.z > 0.0)
    {
        diamond += RbxDiamondPlatePattern(uvZ) * weights.z;
    }
    return diamond;
}

float RbxBlendSlatePattern(float3 position, float3 weights, float broadNoise)
{
    float2 uvX;
    float2 uvY;
    float2 uvZ;
    RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
    float strata = 0.0;
    UNITY_BRANCH if (weights.x > 0.0)
    {
        strata += RbxSlatePattern(uvX, broadNoise) * weights.x;
    }
    UNITY_BRANCH if (weights.y > 0.0)
    {
        strata += RbxSlatePattern(uvY, broadNoise) * weights.y;
    }
    UNITY_BRANCH if (weights.z > 0.0)
    {
        strata += RbxSlatePattern(uvZ, broadNoise) * weights.z;
    }
    return strata;
}

void RbxBlendGrassPattern(float3 position, float3 weights, float grassField,
    out float bladeMask, out float bladeRidge, out float bladeTone)
{
    float2 uvX;
    float2 uvY;
    float2 uvZ;
    RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
    bladeMask = 0.0;
    bladeRidge = 0.0;
    bladeTone = 0.0;

    UNITY_BRANCH if (weights.x > 0.0)
    {
        float axisMask;
        float axisRidge;
        float axisTone;
        RbxGrassPattern(uvX, grassField, axisMask, axisRidge, axisTone);
        bladeMask += axisMask * weights.x;
        bladeRidge += axisRidge * weights.x;
        bladeTone += axisTone * weights.x;
    }

    UNITY_BRANCH if (weights.y > 0.0)
    {
        float axisMask;
        float axisRidge;
        float axisTone;
        RbxGrassPattern(uvY, grassField, axisMask, axisRidge, axisTone);
        bladeMask += axisMask * weights.y;
        bladeRidge += axisRidge * weights.y;
        bladeTone += axisTone * weights.y;
    }

    UNITY_BRANCH if (weights.z > 0.0)
    {
        float axisMask;
        float axisRidge;
        float axisTone;
        RbxGrassPattern(uvZ, grassField, axisMask, axisRidge, axisTone);
        bladeMask += axisMask * weights.z;
        bladeRidge += axisRidge * weights.z;
        bladeTone += axisTone * weights.z;
    }
}

float RbxBlendSandPattern(float3 position, float3 weights, float broadNoise)
{
    float2 uvX;
    float2 uvY;
    float2 uvZ;
    RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
    float ripple = 0.0;
    UNITY_BRANCH if (weights.x > 0.0)
    {
        ripple += RbxSandPattern(uvX, broadNoise) * weights.x;
    }
    UNITY_BRANCH if (weights.y > 0.0)
    {
        ripple += RbxSandPattern(uvY, broadNoise) * weights.y;
    }
    UNITY_BRANCH if (weights.z > 0.0)
    {
        ripple += RbxSandPattern(uvZ, broadNoise) * weights.z;
    }
    return ripple;
}

void RbxBlendGroundPattern(float3 position, float3 weights, out float cracks, out float pebble,
    out float plateTone, out float pebbleTone)
{
    float2 uvX;
    float2 uvY;
    float2 uvZ;
    RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
    cracks = 0.0;
    pebble = 0.0;
    plateTone = 0.0;
    pebbleTone = 0.0;

    UNITY_BRANCH if (weights.x > 0.0)
    {
        float axisCracks;
        float axisPebble;
        float axisPlateTone;
        float axisPebbleTone;
        RbxGroundPattern(uvX, axisCracks, axisPebble, axisPlateTone, axisPebbleTone);
        cracks += axisCracks * weights.x;
        pebble += axisPebble * weights.x;
        plateTone += axisPlateTone * weights.x;
        pebbleTone += axisPebbleTone * weights.x;
    }

    UNITY_BRANCH if (weights.y > 0.0)
    {
        float axisCracks;
        float axisPebble;
        float axisPlateTone;
        float axisPebbleTone;
        RbxGroundPattern(uvY, axisCracks, axisPebble, axisPlateTone, axisPebbleTone);
        cracks += axisCracks * weights.y;
        pebble += axisPebble * weights.y;
        plateTone += axisPlateTone * weights.y;
        pebbleTone += axisPebbleTone * weights.y;
    }

    UNITY_BRANCH if (weights.z > 0.0)
    {
        float axisCracks;
        float axisPebble;
        float axisPlateTone;
        float axisPebbleTone;
        RbxGroundPattern(uvZ, axisCracks, axisPebble, axisPlateTone, axisPebbleTone);
        cracks += axisCracks * weights.z;
        pebble += axisPebble * weights.z;
        plateTone += axisPlateTone * weights.z;
        pebbleTone += axisPebbleTone * weights.z;
    }
}

float RbxBlendFabricPattern(float3 position, float3 weights)
{
    float2 uvX;
    float2 uvY;
    float2 uvZ;
    RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
    float weave = 0.0;
    UNITY_BRANCH if (weights.x > 0.0)
    {
        weave += RbxFabricPattern(uvX) * weights.x;
    }
    UNITY_BRANCH if (weights.y > 0.0)
    {
        weave += RbxFabricPattern(uvY) * weights.y;
    }
    UNITY_BRANCH if (weights.z > 0.0)
    {
        weave += RbxFabricPattern(uvZ) * weights.z;
    }
    return weave;
}

half3 RbxComposeMaterialColor(half3 partColor, half3 materialColor, half partColorInfluence)
{
    half3 partModulation = lerp(half3(1.0h, 1.0h, 1.0h),
        saturate(partColor * 1.15h), saturate(partColorInfluence));
    return max(materialColor * partModulation, half3(0.015h, 0.015h, 0.015h));
}

RbxSurfaceSample RbxEvaluateSurface(float3 patternPosition, float3 patternNormal, int materialMode,
    float patternScale, half3 baseColor)
{
    RbxSurfaceSample sample;
    float3 position = patternPosition * patternScale;
    float broadNoise = RbxFbm(position * 0.72);
    float detailNoise = RbxValueNoise(position * 5.3);
    sample.albedo = baseColor;
    sample.metallic = 0.0h;
    sample.smoothness = 0.4h;
    sample.occlusion = 1.0h;
    sample.height = 0.0;
    sample.heightGradient = float3(0.0, 0.0, 0.0);

    if (materialMode == 0)
    {
        float stipple = RbxValueNoise(position * 3.2);
        sample.albedo = baseColor * (0.91h + stipple * 0.12h);
        sample.smoothness = 0.38h + stipple * 0.06h;
        sample.occlusion = 0.97h;
        sample.height = stipple * 0.08;
    }
    else if (materialMode == 1)
    {
        float polish = RbxValueNoise(position * 1.7);
        sample.albedo = baseColor * (0.96h + polish * 0.06h);
        sample.smoothness = 0.82h;
        sample.height = polish * 0.025;
    }
    else if (materialMode == 2)
    {
        float ringRadius = length(position.xz + (broadNoise - 0.5) * 0.48);
        float rings = 0.5 + 0.5 * sin(ringRadius * 18.0 + RbxFbm(position * 0.36) * 5.0);
        float grain = saturate(rings * 0.74 + detailNoise * 0.26);
        float knot = smoothstep(0.28, 0.0, abs(frac(ringRadius * 0.34) - 0.5));
        sample.albedo = baseColor * lerp(0.42h, 1.18h, grain) * lerp(1.0h, 0.7h, knot);
        sample.smoothness = 0.25h + grain * 0.10h;
        sample.occlusion = 0.84h + grain * 0.15h;
        sample.height = grain * 0.72 + knot * 0.12;
    }
    else if (materialMode == 3)
    {
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float seam;
        float grain;
        float plankTone;
        RbxBlendWoodPlankPattern(position, weights, seam, grain, plankTone);
        sample.albedo = lerp(baseColor * lerp(0.5h, 1.12h, grain) * plankTone,
            baseColor * 0.18h, seam);
        sample.smoothness = lerp(0.28h, 0.08h, seam);
        sample.occlusion = lerp(0.96h, 0.52h, seam);
        sample.height = (1.0 - seam) * (0.58 + grain * 0.28);
    }
    else if (materialMode == 4)
    {
        float metalVariation = RbxFbm(position * 1.45 + float3(19.0, 7.0, 31.0));
        float microVariation = RbxValueNoise(position * 10.0 + float3(3.0, 17.0, 11.0));
        sample.albedo = baseColor * (0.87h + metalVariation * 0.16h + microVariation * 0.035h);
        sample.metallic = 0.97h;
        sample.smoothness = 0.76h + metalVariation * 0.055h - microVariation * 0.035h;
        sample.occlusion = 0.98h;
        sample.height = (metalVariation - 0.5) * 0.075 + (microVariation - 0.5) * 0.02;
    }
    else if (materialMode == 5)
    {
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float diamond = RbxBlendDiamondPlatePattern(position, weights);
        float machining = 0.88 + RbxValueNoise(position * 7.0) * 0.15;
        sample.albedo = baseColor * machining * lerp(0.64h, 1.2h, diamond);
        sample.metallic = 0.98h;
        sample.smoothness = lerp(0.48h, 0.7h, diamond);
        sample.occlusion = lerp(0.86h, 1.0h, diamond);
        sample.height = diamond;
    }
    else if (materialMode == 6)
    {
        float corrosion = smoothstep(0.43, 0.72, broadNoise + detailNoise * 0.2);
        float pits = smoothstep(0.77, 0.93, RbxValueNoise(position * 8.0)) * corrosion;
        half3 rustColor = baseColor * half3(1.12h, 0.42h, 0.12h);
        sample.albedo = lerp(baseColor * (0.58h + detailNoise * 0.3h), rustColor, corrosion);
        sample.albedo *= lerp(1.0h, 0.42h, pits);
        sample.metallic = lerp(0.9h, 0.04h, corrosion);
        sample.smoothness = lerp(0.56h, 0.13h, corrosion);
        sample.occlusion = lerp(0.94h, 0.58h, pits);
        sample.height = broadNoise * 0.38 - pits * 0.68;
    }
    else if (materialMode == 7)
    {
        float4 marbleField = RbxSimplexFbmGrad(position * 0.42 + float3(13.0, 29.0, 47.0));
        float ribbonCoordinate = dot(position, float3(0.78, 0.18, 0.42))
            + (marbleField.w - 0.5) * 2.7;
        float ribbonWave = abs(sin(ribbonCoordinate * 2.15));
        float veinHalo = 1.0 - smoothstep(0.12, 0.48, ribbonWave);
        float mainVein = 1.0 - smoothstep(0.035, 0.2, ribbonWave);
        float branchCoordinate = dot(position, float3(-0.24, 0.62, 0.51))
            + marbleField.w * 1.35;
        float branchWave = abs(sin(branchCoordinate * 1.45 + 1.2));
        float branchVein = (1.0 - smoothstep(0.055, 0.24, branchWave))
            * smoothstep(0.52, 0.78, marbleField.w);
        float combinedVein = saturate(mainVein + branchVein * 0.42);
        half3 stoneColor = baseColor * (0.93h + marbleField.w * 0.12h);
        half3 haloColor = lerp(stoneColor, baseColor * 0.69h, (half)veinHalo * 0.58h);
        half3 veinColor = lerp(baseColor * 0.42h, half3(0.31h, 0.29h, 0.27h), 0.28h);
        sample.albedo = lerp(haloColor, veinColor, combinedVein);
        sample.smoothness = 0.72h - combinedVein * 0.11h;
        sample.occlusion = 0.97h;
        sample.height = -combinedVein * 0.075 - veinHalo * 0.018;
        sample.heightGradient = marbleField.xyz * (0.42 * 0.035);
    }
    else if (materialMode == 8)
    {
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float strata = RbxBlendSlatePattern(position, weights, broadNoise);
        float split = smoothstep(0.82, 0.96, strata);
        sample.albedo = baseColor * (0.48h + broadNoise * 0.48h) * lerp(1.0h, 0.48h, split);
        sample.smoothness = 0.22h + broadNoise * 0.1h;
        sample.occlusion = lerp(0.92h, 0.64h, split);
        sample.height = strata * 0.44 - split * 0.28;
    }
    else if (materialMode == 9)
    {
        float aggregate = RbxValueNoise(position * 3.4);
        float pits = smoothstep(0.86, 0.97, RbxValueNoise(position * 10.0));
        float flecks = smoothstep(0.78, 0.9, aggregate);
        sample.albedo = baseColor * (0.66h + broadNoise * 0.42h);
        sample.albedo = lerp(sample.albedo, baseColor * 1.32h, flecks * 0.35);
        sample.albedo *= lerp(1.0h, 0.42h, pits);
        sample.smoothness = 0.15h;
        sample.occlusion = lerp(0.92h, 0.5h, pits);
        sample.height = broadNoise * 0.42 + aggregate * 0.16 - pits * 0.7;
    }
    else if (materialMode == 10)
    {
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float mortar;
        float brickTone;
        RbxBlendBrickPattern(position, weights, mortar, brickTone);
        half3 brickColor = baseColor * brickTone * (0.83h + detailNoise * 0.23h);
        half3 mortarColor = lerp(baseColor * 0.34h, half3(0.42h, 0.4h, 0.36h), 0.52h);
        sample.albedo = lerp(brickColor, mortarColor, mortar);
        sample.smoothness = lerp(0.2h, 0.08h, mortar);
        sample.occlusion = lerp(0.96h, 0.6h, mortar);
        sample.height = (1.0 - mortar) * (0.68 + broadNoise * 0.16);
    }
    else if (materialMode == 11)
    {
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float stoneMask;
        float stoneDome;
        float stoneTone;
        RbxBlendCobblestonePattern(position, weights, stoneMask, stoneDome, stoneTone);
        half3 jointColor = baseColor * 0.2h;
        half3 stoneColor = baseColor * stoneTone * (0.9h + broadNoise * 0.18h);
        sample.albedo = lerp(jointColor, stoneColor, stoneMask);
        sample.smoothness = lerp(0.07h, 0.2h, stoneMask);
        sample.occlusion = lerp(0.46h, 0.97h, stoneMask);
        sample.height = stoneDome * 0.86;
    }
    else if (materialMode == 12)
    {
        float4 grassField = RbxSimplexFbmGrad(position * 0.48 + float3(5.0, 17.0, 31.0));
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float bladeMask;
        float bladeRidge;
        float bladeTone;
        RbxBlendGrassPattern(position, weights, grassField.w, bladeMask, bladeRidge, bladeTone);
        float clumps = saturate(grassField.w * 0.78 + broadNoise * 0.22);
        half3 thatchColor = baseColor * (0.27h + clumps * 0.12h);
        half3 bladeColor = baseColor * (0.74h + clumps * 0.36h + bladeTone * 0.28h);
        sample.albedo = lerp(thatchColor, bladeColor, bladeMask);
        sample.albedo = lerp(sample.albedo, baseColor * 1.48h, bladeRidge * 0.46);
        sample.smoothness = 0.11h + bladeMask * 0.1h;
        sample.occlusion = lerp(0.57h, 0.98h, bladeMask);
        sample.height = bladeMask * (0.52 + bladeTone * 0.24) + bladeRidge * 0.16;
        sample.heightGradient = grassField.xyz * (0.48 * 0.055);
    }
    else if (materialMode == 13)
    {
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float ripple = RbxBlendSandPattern(position, weights, broadNoise);
        float grains = RbxValueNoise(position * 12.0);
        sample.albedo = baseColor * (0.72h + ripple * 0.22h + grains * 0.14h);
        sample.smoothness = 0.18h;
        sample.occlusion = 0.88h + ripple * 0.1h;
        sample.height = ripple * 0.56 + grains * 0.18;
    }
    else if (materialMode == 14)
    {
        float4 earthField = RbxSimplexFbmGrad(position * 0.5 + float3(37.0, 11.0, 23.0));
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float cracks;
        float pebble;
        float plateTone;
        float pebbleTone;
        RbxBlendGroundPattern(position, weights, cracks, pebble, plateTone, pebbleTone);
        half3 earthColor = baseColor
            * (0.72h + earthField.w * 0.16h + plateTone * 0.16h);
        half3 crackColor = baseColor * 0.22h;
        half3 stoneColor = lerp(baseColor * 1.17h, half3(0.28h, 0.25h, 0.2h), 0.34h)
            * (0.86h + pebbleTone * 0.25h);
        sample.albedo = lerp(earthColor, crackColor, cracks);
        sample.albedo = lerp(sample.albedo, stoneColor, pebble);
        sample.smoothness = lerp(0.13h, 0.055h, cracks);
        sample.smoothness = lerp(sample.smoothness, 0.2h, pebble);
        sample.occlusion = lerp(0.93h, 0.48h, cracks);
        sample.occlusion = lerp(sample.occlusion, 0.98h, pebble);
        sample.height = 0.1 - cracks * 0.4 + pebble * (0.62 + pebbleTone * 0.18);
        sample.heightGradient = earthField.xyz * (0.5 * 0.075);
    }
    else if (materialMode == 15)
    {
        float ridges = RbxFbm(position * 0.48 + broadNoise * 1.4);
        float mineral = smoothstep(0.62, 0.84, detailNoise);
        sample.albedo = baseColor * (0.42h + ridges * 0.65h);
        sample.albedo = lerp(sample.albedo, baseColor * 1.22h, mineral * 0.28);
        sample.smoothness = 0.16h + mineral * 0.08h;
        sample.occlusion = 0.62h + ridges * 0.36h;
        sample.height = ridges * 0.82 + detailNoise * 0.18;
    }
    else if (materialMode == 16)
    {
        float crystals = pow(max(RbxValueNoise(position * 7.0), 0.0), 2.4);
        float drifts = RbxFbm(position * 0.5);
        half blueShadow = (half)(1.0 - drifts) * 0.08h;
        sample.albedo = baseColor * (0.91h + crystals * 0.13h) + half3(0.0h, blueShadow * 0.5h, blueShadow);
        sample.smoothness = 0.34h + crystals * 0.16h;
        sample.occlusion = 0.9h + drifts * 0.1h;
        sample.height = drifts * 0.5 + crystals * 0.34;
    }
    else if (materialMode == 17)
    {
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float weave = RbxBlendFabricPattern(position, weights);
        sample.albedo = baseColor * (0.68h + weave * 0.42h);
        sample.smoothness = 0.2h;
        sample.occlusion = 0.82h + weave * 0.16h;
        sample.height = weave * 0.72;
    }
    else
    {
        float invalidPattern = step(0.5, frac(dot(position, float3(1.0, 0.73, 1.31)) * 2.0));
        sample.albedo = lerp(half3(0.03h, 0.0h, 0.03h), half3(1.0h, 0.0h, 0.8h), invalidPattern);
        sample.smoothness = 0.15h;
        sample.occlusion = 1.0h;
        sample.height = invalidPattern * 0.2;
    }

    return sample;
}

float3 RbxPerturbNormal(float3 positionWS, float3 normalWS, float3 heightGradient,
    bool objectAlignedProjection, float patternScale, float bumpStrength, float centerHeight)
{
    float3 positionDerivativeX = ddx(positionWS);
    float3 positionDerivativeY = ddy(positionWS);
    float heightDerivativeX = ddx(centerHeight);
    float heightDerivativeY = ddy(centerHeight);
    float3 reciprocalX = cross(positionDerivativeY, normalWS);
    float3 reciprocalY = cross(normalWS, positionDerivativeX);
    float determinant = dot(positionDerivativeX, reciprocalX);
    float inverseDeterminant = sign(determinant) / max(abs(determinant), 0.00001);
    float3 screenGradient = (heightDerivativeX * reciprocalX + heightDerivativeY * reciprocalY)
        * inverseDeterminant;

    float3 referenceAxis = abs(normalWS.y) < 0.92 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 tangentWS = normalize(cross(referenceAxis, normalWS));
    float3 bitangentWS = normalize(cross(normalWS, tangentWS));
    float3 tangentPattern = tangentWS;
    float3 bitangentPattern = bitangentWS;
    if (objectAlignedProjection)
    {
        float3 objectScale = RbxObjectAxisScale();
        float3x3 worldToObject = (float3x3)GetWorldToObjectMatrix();
        tangentPattern = mul(worldToObject, tangentWS) * objectScale;
        bitangentPattern = mul(worldToObject, bitangentWS) * objectScale;
    }

    float3 scaledGradient = heightGradient * patternScale;
    float tangentSlope = dot(scaledGradient, tangentPattern);
    float bitangentSlope = dot(scaledGradient, bitangentPattern);
    float3 analyticalGradient = tangentWS * tangentSlope + bitangentWS * bitangentSlope;
    return normalize(normalWS - (screenGradient + analyticalGradient) * bumpStrength);
}

#endif
