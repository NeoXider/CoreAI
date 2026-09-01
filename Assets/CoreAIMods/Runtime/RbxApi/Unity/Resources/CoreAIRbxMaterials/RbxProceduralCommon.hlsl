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

float RbxPixelFootprint(float3 position)
{
    float3 footprint = fwidth(position);
    return max(footprint.x, max(footprint.y, footprint.z));
}

float2 RbxUvFootprint(float2 uv)
{
    return fwidth(uv);
}

float RbxFrequencyVisibility(float footprint)
{
    return 1.0 - smoothstep(0.35, 0.85, footprint);
}

float RbxFilteredValueNoise(float3 position, out float visibility)
{
    visibility = RbxFrequencyVisibility(RbxPixelFootprint(position));
    return lerp(0.5, RbxValueNoise(position), visibility);
}

float RbxFilteredWave(float phase, float phaseFootprint, out float visibility)
{
    visibility = 1.0 - smoothstep(1.0, 3.14159265, phaseFootprint);
    return 0.5 + 0.5 * sin(phase) * visibility;
}

float RbxFilteredInsideMask(float distanceValue, float threshold, float minimumHalfWidth,
    float distanceFootprint)
{
    float halfWidth = max(minimumHalfWidth, distanceFootprint * 0.5);
    return 1.0 - smoothstep(threshold - halfWidth, threshold + halfWidth, distanceValue);
}

half RbxCompensateUnresolvedRoughness(half smoothness, float visibility, half variance)
{
    return saturate(smoothness - (half)(1.0 - visibility) * variance);
}

float RbxFbm(float3 position)
{
    float footprint = RbxPixelFootprint(position);
    float visibility = RbxFrequencyVisibility(footprint);
    float octaveValue = 0.5;
    UNITY_BRANCH if (visibility > 0.0)
    {
        octaveValue = RbxValueNoise(position);
    }
    float value = lerp(0.5, octaveValue, visibility) * 0.5333;

    position = position * 2.03 + 17.17;
    footprint *= 2.03;
    visibility = RbxFrequencyVisibility(footprint);
    octaveValue = 0.5;
    UNITY_BRANCH if (visibility > 0.0)
    {
        octaveValue = RbxValueNoise(position);
    }
    value += lerp(0.5, octaveValue, visibility) * 0.2667;

    position = position * 2.01 + 9.23;
    footprint *= 2.01;
    visibility = RbxFrequencyVisibility(footprint);
    octaveValue = 0.5;
    UNITY_BRANCH if (visibility > 0.0)
    {
        octaveValue = RbxValueNoise(position);
    }
    value += lerp(0.5, octaveValue, visibility) * 0.1333;

    position = position * 2.04 + 5.71;
    footprint *= 2.04;
    visibility = RbxFrequencyVisibility(footprint);
    octaveValue = 0.5;
    UNITY_BRANCH if (visibility > 0.0)
    {
        octaveValue = RbxValueNoise(position);
    }
    value += lerp(0.5, octaveValue, visibility) * 0.0667;
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
    return materialMode == 3 || materialMode == 4 || materialMode == 5 || materialMode == 8 ||
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

void RbxWoodPlankPattern(float2 uv, float2 uvFootprint, float seed, out float seam,
    out float grain, out float plankTone)
{
    float row = floor(uv.y * 1.35);
    float stagger = fmod(abs(row), 2.0) * 0.5;
    float cellX = floor(uv.x * 0.42 + stagger);
    float2 plankCell = frac(float2(uv.x * 0.42 + stagger, uv.y * 1.35));
    float cellFootprint = max(uvFootprint.x * 0.42, uvFootprint.y * 1.35);
    seam = RbxFilteredInsideMask(RbxCellEdge(plankCell), 0.0365, 0.0185,
        cellFootprint);
    float grainNoise = RbxValueNoise(float3(uv * 0.8, seed));
    float grainPhase = uv.x * 22.0 + grainNoise * 6.0;
    float grainVisibility;
    grain = RbxFilteredWave(grainPhase, uvFootprint.x * 22.0, grainVisibility);
    plankTone = 0.88 + RbxHash31(float3(cellX, row, 4.0 + seed)) * 0.16;
}

void RbxBrickPattern(float2 uv, float2 uvFootprint, float seed, out float mortar,
    out float brickTone)
{
    float row = floor(uv.y * 1.28);
    float stagger = fmod(abs(row), 2.0) * 0.5;
    float cellX = floor(uv.x * 0.7 + stagger);
    float2 brickCell = frac(float2(uv.x * 0.7 + stagger, uv.y * 1.28));
    float cellFootprint = max(uvFootprint.x * 0.7, uvFootprint.y * 1.28);
    mortar = RbxFilteredInsideMask(RbxCellEdge(brickCell), 0.05, 0.025,
        cellFootprint);
    brickTone = 0.86 + RbxHash31(float3(cellX, row, 9.0 + seed)) * 0.18;
}

void RbxCobblestonePattern(float2 uv, float2 uvFootprint, float seed, out float stoneMask,
    out float dome, out float stoneTone)
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
    float distanceFootprint = max(uvFootprint.x * 1.55 / width,
        uvFootprint.y * 1.35 / height);
    stoneMask = RbxFilteredInsideMask(stoneDistance, 0.91, 0.09, distanceFootprint);
    dome = saturate(1.0 - stoneDistance * stoneDistance) * stoneMask;
    stoneTone = 0.86 + RbxHash31(float3(cell, 59.0 + seed)) * 0.16;
}

float3 RbxGrassBladeLayer(float2 uv, float2 uvFootprint, float density, float seed)
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
    float bladeFootprint = max(uvFootprint.x, uvFootprint.y) * density;
    float progressHalfWidth = max(0.04, bladeFootprint / (4.0 * halfLength));
    float lengthMask = smoothstep(0.04 - progressHalfWidth, 0.04 + progressHalfWidth,
        bladeProgress) * (1.0 - smoothstep(0.92 - progressHalfWidth,
            0.92 + progressHalfWidth, bladeProgress));
    float profile = sqrt(max(1.0 - abs(bladeProgress * 2.0 - 1.0), 0.0));
    float baseWidth = 0.055 + RbxHash31(float3(cell, seed + 59.0)) * 0.032;
    float lean = (RbxHash31(float3(cell, seed + 71.0)) - 0.5) * 0.24;
    float center = lean * (bladeProgress * bladeProgress - 0.18);
    float halfWidth = baseWidth * profile;
    float crossBlade = abs(bladePosition.x - center);
    float blade = RbxFilteredInsideMask(crossBlade, halfWidth, 0.009,
        bladeFootprint) * lengthMask;
    float ridge = RbxFilteredInsideMask(crossBlade, 0.012, 0.006,
        bladeFootprint) * blade
        * smoothstep(0.12, 0.72, bladeProgress);
    float tone = RbxHash31(float3(cell, seed + 89.0));
    return float3(blade, ridge, tone);
}

void RbxGroundPattern(float2 uv, float2 uvFootprint, out float cracks, out float pebble,
    out float plateTone, out float pebbleTone)
{
    float2 crackedUv = uv * 1.18;
    crackedUv.x += sin(crackedUv.y * 1.73 + sin(crackedUv.y * 0.47)) * 0.13;
    crackedUv.y += sin(crackedUv.x * 1.31 + sin(crackedUv.x * 0.59)) * 0.11;
    float2 plateCell = floor(crackedUv);
    float2 platePosition = frac(crackedUv);
    float edgeDistance = RbxCellEdge(platePosition);
    float plateFootprint = max(uvFootprint.x, uvFootprint.y) * 1.18;
    cracks = RbxFilteredInsideMask(edgeDistance, 0.047, 0.025, plateFootprint);

    float2 centeredPlate = platePosition - 0.5;
    float fractureDirection = RbxHash31(float3(plateCell, 113.0)) < 0.5 ? -1.0 : 1.0;
    float fractureDistance = abs(centeredPlate.x + centeredPlate.y * fractureDirection);
    float fractureLength = 1.0 - smoothstep(0.24, 0.46, abs(centeredPlate.y));
    float fracture = RbxFilteredInsideMask(fractureDistance, 0.026, 0.014,
        plateFootprint) * fractureLength;
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
    float pebbleFootprint = max(uvFootprint.x, uvFootprint.y) * 4.1;
    pebble = RbxFilteredInsideMask(length(pebblePosition), pebbleRadius + 0.0175,
        0.0175, pebbleFootprint);
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
    float2 footprintX = RbxUvFootprint(uvX);
    float2 footprintY = RbxUvFootprint(uvY);
    float2 footprintZ = RbxUvFootprint(uvZ);
    seam = 0.0;
    grain = 0.0;
    plankTone = 0.0;

    UNITY_BRANCH if (weights.x > 0.0)
    {
        float axisSeam;
        float axisGrain;
        float axisTone;
        RbxWoodPlankPattern(uvX, footprintX, 0.0, axisSeam, axisGrain, axisTone);
        seam += axisSeam * weights.x;
        grain += axisGrain * weights.x;
        plankTone += axisTone * weights.x;
    }

    UNITY_BRANCH if (weights.y > 0.0)
    {
        float axisSeam;
        float axisGrain;
        float axisTone;
        RbxWoodPlankPattern(uvY, footprintY, 7.0, axisSeam, axisGrain, axisTone);
        seam += axisSeam * weights.y;
        grain += axisGrain * weights.y;
        plankTone += axisTone * weights.y;
    }

    UNITY_BRANCH if (weights.z > 0.0)
    {
        float axisSeam;
        float axisGrain;
        float axisTone;
        RbxWoodPlankPattern(uvZ, footprintZ, 13.0, axisSeam, axisGrain, axisTone);
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
    float2 footprintX = RbxUvFootprint(uvX);
    float2 footprintY = RbxUvFootprint(uvY);
    float2 footprintZ = RbxUvFootprint(uvZ);
    mortar = 0.0;
    brickTone = 0.0;

    UNITY_BRANCH if (weights.x > 0.0)
    {
        float axisMortar;
        float axisTone;
        RbxBrickPattern(uvX, footprintX, 0.0, axisMortar, axisTone);
        mortar += axisMortar * weights.x;
        brickTone += axisTone * weights.x;
    }

    UNITY_BRANCH if (weights.y > 0.0)
    {
        float axisMortar;
        float axisTone;
        RbxBrickPattern(uvY, footprintY, 7.0, axisMortar, axisTone);
        mortar += axisMortar * weights.y;
        brickTone += axisTone * weights.y;
    }

    UNITY_BRANCH if (weights.z > 0.0)
    {
        float axisMortar;
        float axisTone;
        RbxBrickPattern(uvZ, footprintZ, 13.0, axisMortar, axisTone);
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
    float2 footprintX = RbxUvFootprint(uvX);
    float2 footprintY = RbxUvFootprint(uvY);
    float2 footprintZ = RbxUvFootprint(uvZ);
    stoneMask = 0.0;
    stoneDome = 0.0;
    stoneTone = 0.0;

    UNITY_BRANCH if (weights.x > 0.0)
    {
        float axisMask;
        float axisDome;
        float axisTone;
        RbxCobblestonePattern(uvX, footprintX, 0.0, axisMask, axisDome, axisTone);
        stoneMask += axisMask * weights.x;
        stoneDome += axisDome * weights.x;
        stoneTone += axisTone * weights.x;
    }

    UNITY_BRANCH if (weights.y > 0.0)
    {
        float axisMask;
        float axisDome;
        float axisTone;
        RbxCobblestonePattern(uvY, footprintY, 7.0, axisMask, axisDome, axisTone);
        stoneMask += axisMask * weights.y;
        stoneDome += axisDome * weights.y;
        stoneTone += axisTone * weights.y;
    }

    UNITY_BRANCH if (weights.z > 0.0)
    {
        float axisMask;
        float axisDome;
        float axisTone;
        RbxCobblestonePattern(uvZ, footprintZ, 13.0, axisMask, axisDome, axisTone);
        stoneMask += axisMask * weights.z;
        stoneDome += axisDome * weights.z;
        stoneTone += axisTone * weights.z;
    }
}

void RbxDiamondPlatePattern(float2 uv, float2 uvFootprint, out float tread, out float edge)
{
    float2 plateCell = frac(uv * 1.35) - 0.5;
    float2 diamondCoordinates = float2(plateCell.x + plateCell.y, plateCell.x - plateCell.y);
    float diamondDistance = max(abs(diamondCoordinates.x), abs(diamondCoordinates.y));
    float distanceFootprint = (uvFootprint.x + uvFootprint.y) * 1.35;
    tread = RbxFilteredInsideMask(diamondDistance, 0.28, 0.06, distanceFootprint);
    float outer = RbxFilteredInsideMask(diamondDistance, 0.36, 0.035, distanceFootprint);
    float inner = RbxFilteredInsideMask(diamondDistance, 0.2, 0.035, distanceFootprint);
    edge = saturate(outer - inner);
}

float RbxDirectionalMachiningPattern(float2 uv, float2 uvFootprint, float frequency,
    out float visibility)
{
    float phase = uv.x * frequency * 6.28318531 + sin(uv.y * 3.7) * 0.18;
    float phaseFootprint = uvFootprint.x * frequency * 6.28318531
        + uvFootprint.y * 3.7 * 0.18;
    return RbxFilteredWave(phase, phaseFootprint, visibility);
}

float RbxSlatePattern(float2 uv, float2 uvFootprint, float broadNoise)
{
    float phase = (uv.y + broadNoise * 0.42) * 13.0;
    float visibility;
    return RbxFilteredWave(phase, uvFootprint.y * 13.0, visibility);
}

void RbxGrassPattern(float2 uv, float2 uvFootprint, float grassField, out float bladeMask,
    out float bladeRidge, out float bladeTone)
{
    float2 grassUv = uv + float2(grassField - 0.5, 0.5 - grassField) * 0.08;
    float3 bladeA = RbxGrassBladeLayer(grassUv, uvFootprint, 3.2, 5.0);
    float2 rotatedUv = float2(grassUv.x * 0.819 - grassUv.y * 0.574,
        grassUv.x * 0.574 + grassUv.y * 0.819);
    float2 rotatedFootprint = float2(dot(uvFootprint, float2(0.819, 0.574)),
        dot(uvFootprint, float2(0.574, 0.819)));
    float3 bladeB = RbxGrassBladeLayer(rotatedUv + float2(4.3, 7.1),
        rotatedFootprint, 4.7, 71.0);
    bladeMask = saturate(max(bladeA.x, bladeB.x * 0.92));
    bladeRidge = saturate(max(bladeA.y, bladeB.y * 0.86));
    bladeTone = max(bladeA.z * bladeA.x, bladeB.z * bladeB.x);
}

float RbxSandPattern(float2 uv, float2 uvFootprint, float broadNoise)
{
    float phase = uv.x * 7.5 + sin(uv.y * 1.9) * 1.4 + broadNoise * 2.0;
    float phaseFootprint = uvFootprint.x * 7.5 + uvFootprint.y * 1.9 * 1.4;
    float visibility;
    return RbxFilteredWave(phase, phaseFootprint, visibility);
}

void RbxFabricPattern(float2 uv, float2 uvFootprint, out float weave,
    out float visibility)
{
    float warpVisibility;
    float weftVisibility;
    float warp = RbxFilteredWave(uv.x * 42.0, uvFootprint.x * 42.0, warpVisibility);
    float weft = RbxFilteredWave(uv.y * 42.0 + 1.5708, uvFootprint.y * 42.0,
        weftVisibility);
    weave = warp * 0.52 + weft * 0.48;
    visibility = warpVisibility * 0.52 + weftVisibility * 0.48;
}

void RbxBlendDiamondPlatePattern(float3 position, float3 weights, out float tread,
    out float edge)
{
    float2 uvX;
    float2 uvY;
    float2 uvZ;
    RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
    float2 footprintX = RbxUvFootprint(uvX);
    float2 footprintY = RbxUvFootprint(uvY);
    float2 footprintZ = RbxUvFootprint(uvZ);
    tread = 0.0;
    edge = 0.0;
    UNITY_BRANCH if (weights.x > 0.0)
    {
        float axisTread;
        float axisEdge;
        RbxDiamondPlatePattern(uvX, footprintX, axisTread, axisEdge);
        tread += axisTread * weights.x;
        edge += axisEdge * weights.x;
    }
    UNITY_BRANCH if (weights.y > 0.0)
    {
        float axisTread;
        float axisEdge;
        RbxDiamondPlatePattern(uvY, footprintY, axisTread, axisEdge);
        tread += axisTread * weights.y;
        edge += axisEdge * weights.y;
    }
    UNITY_BRANCH if (weights.z > 0.0)
    {
        float axisTread;
        float axisEdge;
        RbxDiamondPlatePattern(uvZ, footprintZ, axisTread, axisEdge);
        tread += axisTread * weights.z;
        edge += axisEdge * weights.z;
    }
}

void RbxBlendDirectionalMachiningPattern(float3 position, float3 weights, float frequency,
    out float machining, out float visibility)
{
    float2 uvX;
    float2 uvY;
    float2 uvZ;
    RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
    float2 footprintX = RbxUvFootprint(uvX);
    float2 footprintY = RbxUvFootprint(uvY);
    float2 footprintZ = RbxUvFootprint(uvZ);
    machining = 0.0;
    visibility = 0.0;
    UNITY_BRANCH if (weights.x > 0.0)
    {
        float axisVisibility;
        machining += RbxDirectionalMachiningPattern(uvX, footprintX, frequency,
            axisVisibility) * weights.x;
        visibility += axisVisibility * weights.x;
    }
    UNITY_BRANCH if (weights.y > 0.0)
    {
        float axisVisibility;
        machining += RbxDirectionalMachiningPattern(uvY, footprintY, frequency,
            axisVisibility) * weights.y;
        visibility += axisVisibility * weights.y;
    }
    UNITY_BRANCH if (weights.z > 0.0)
    {
        float axisVisibility;
        machining += RbxDirectionalMachiningPattern(uvZ, footprintZ, frequency,
            axisVisibility) * weights.z;
        visibility += axisVisibility * weights.z;
    }
}

float RbxBlendSlatePattern(float3 position, float3 weights, float broadNoise)
{
    float2 uvX;
    float2 uvY;
    float2 uvZ;
    RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
    float2 footprintX = RbxUvFootprint(uvX);
    float2 footprintY = RbxUvFootprint(uvY);
    float2 footprintZ = RbxUvFootprint(uvZ);
    float strata = 0.0;
    UNITY_BRANCH if (weights.x > 0.0)
    {
        strata += RbxSlatePattern(uvX, footprintX, broadNoise) * weights.x;
    }
    UNITY_BRANCH if (weights.y > 0.0)
    {
        strata += RbxSlatePattern(uvY, footprintY, broadNoise) * weights.y;
    }
    UNITY_BRANCH if (weights.z > 0.0)
    {
        strata += RbxSlatePattern(uvZ, footprintZ, broadNoise) * weights.z;
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
    float2 footprintX = RbxUvFootprint(uvX);
    float2 footprintY = RbxUvFootprint(uvY);
    float2 footprintZ = RbxUvFootprint(uvZ);
    bladeMask = 0.0;
    bladeRidge = 0.0;
    bladeTone = 0.0;

    UNITY_BRANCH if (weights.x > 0.0)
    {
        float axisMask;
        float axisRidge;
        float axisTone;
        RbxGrassPattern(uvX, footprintX, grassField, axisMask, axisRidge, axisTone);
        bladeMask += axisMask * weights.x;
        bladeRidge += axisRidge * weights.x;
        bladeTone += axisTone * weights.x;
    }

    UNITY_BRANCH if (weights.y > 0.0)
    {
        float axisMask;
        float axisRidge;
        float axisTone;
        RbxGrassPattern(uvY, footprintY, grassField, axisMask, axisRidge, axisTone);
        bladeMask += axisMask * weights.y;
        bladeRidge += axisRidge * weights.y;
        bladeTone += axisTone * weights.y;
    }

    UNITY_BRANCH if (weights.z > 0.0)
    {
        float axisMask;
        float axisRidge;
        float axisTone;
        RbxGrassPattern(uvZ, footprintZ, grassField, axisMask, axisRidge, axisTone);
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
    float2 footprintX = RbxUvFootprint(uvX);
    float2 footprintY = RbxUvFootprint(uvY);
    float2 footprintZ = RbxUvFootprint(uvZ);
    float ripple = 0.0;
    UNITY_BRANCH if (weights.x > 0.0)
    {
        ripple += RbxSandPattern(uvX, footprintX, broadNoise) * weights.x;
    }
    UNITY_BRANCH if (weights.y > 0.0)
    {
        ripple += RbxSandPattern(uvY, footprintY, broadNoise) * weights.y;
    }
    UNITY_BRANCH if (weights.z > 0.0)
    {
        ripple += RbxSandPattern(uvZ, footprintZ, broadNoise) * weights.z;
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
    float2 footprintX = RbxUvFootprint(uvX);
    float2 footprintY = RbxUvFootprint(uvY);
    float2 footprintZ = RbxUvFootprint(uvZ);
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
        RbxGroundPattern(uvX, footprintX, axisCracks, axisPebble, axisPlateTone,
            axisPebbleTone);
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
        RbxGroundPattern(uvY, footprintY, axisCracks, axisPebble, axisPlateTone,
            axisPebbleTone);
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
        RbxGroundPattern(uvZ, footprintZ, axisCracks, axisPebble, axisPlateTone,
            axisPebbleTone);
        cracks += axisCracks * weights.z;
        pebble += axisPebble * weights.z;
        plateTone += axisPlateTone * weights.z;
        pebbleTone += axisPebbleTone * weights.z;
    }
}

void RbxBlendFabricPattern(float3 position, float3 weights, out float weave,
    out float visibility)
{
    float2 uvX;
    float2 uvY;
    float2 uvZ;
    RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
    float2 footprintX = RbxUvFootprint(uvX);
    float2 footprintY = RbxUvFootprint(uvY);
    float2 footprintZ = RbxUvFootprint(uvZ);
    weave = 0.0;
    visibility = 0.0;
    UNITY_BRANCH if (weights.x > 0.0)
    {
        float axisWeave;
        float axisVisibility;
        RbxFabricPattern(uvX, footprintX, axisWeave, axisVisibility);
        weave += axisWeave * weights.x;
        visibility += axisVisibility * weights.x;
    }
    UNITY_BRANCH if (weights.y > 0.0)
    {
        float axisWeave;
        float axisVisibility;
        RbxFabricPattern(uvY, footprintY, axisWeave, axisVisibility);
        weave += axisWeave * weights.y;
        visibility += axisVisibility * weights.y;
    }
    UNITY_BRANCH if (weights.z > 0.0)
    {
        float axisWeave;
        float axisVisibility;
        RbxFabricPattern(uvZ, footprintZ, axisWeave, axisVisibility);
        weave += axisWeave * weights.z;
        visibility += axisVisibility * weights.z;
    }
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
    float3 physicalPosition = patternPosition;
    float3 position = physicalPosition * patternScale;
    float broadNoise = RbxFbm(physicalPosition * 0.72);
    float detailVisibility;
    float detailNoise = RbxFilteredValueNoise(physicalPosition * 5.3,
        detailVisibility);
    sample.albedo = baseColor;
    sample.metallic = 0.0h;
    sample.smoothness = 0.4h;
    sample.occlusion = 1.0h;
    sample.height = 0.0;
    sample.heightGradient = float3(0.0, 0.0, 0.0);

    if (materialMode == 0)
    {
        float stippleVisibility;
        float stipple = RbxFilteredValueNoise(physicalPosition * 120.0,
            stippleVisibility);
        sample.albedo = baseColor * (0.99h + (half)(stipple - 0.5) * 0.025h);
        sample.smoothness = 0.42h + (half)(stipple - 0.5) * 0.12h;
        sample.smoothness = RbxCompensateUnresolvedRoughness(sample.smoothness,
            stippleVisibility, 0.04h);
        sample.occlusion = 0.97h;
        sample.height = (stipple - 0.5) * 0.025;
    }
    else if (materialMode == 1)
    {
        float polishVisibility;
        float polish = RbxFilteredValueNoise(physicalPosition * 2.5,
            polishVisibility);
        float microVisibility;
        float microRoughness = RbxFilteredValueNoise(
            physicalPosition * 220.0 + float3(17.0, 43.0, 11.0), microVisibility);
        sample.albedo = baseColor * (0.995h + (half)(polish - 0.5) * 0.02h);
        sample.smoothness = 0.84h + (half)(polish - 0.5) * 0.04h
            - (half)(microRoughness - 0.5) * 0.12h;
        sample.smoothness = RbxCompensateUnresolvedRoughness(sample.smoothness,
            microVisibility, 0.055h);
        sample.height = (microRoughness - 0.5) * 0.018;
    }
    else if (materialMode == 2)
    {
        float ringRadius = length(position.xz + (broadNoise - 0.5) * 0.48);
        float ringPhase = ringRadius * 18.0 + RbxFbm(position * 0.36) * 5.0;
        float ringVisibility;
        float rings = RbxFilteredWave(ringPhase, fwidth(ringPhase), ringVisibility);
        float grain = saturate(rings * 0.74 + detailNoise * 0.26);
        float knot = smoothstep(0.28, 0.0, abs(frac(ringRadius * 0.34) - 0.5));
        float poreVisibility;
        float poreRoughness = RbxFilteredValueNoise(
            physicalPosition * 180.0 + float3(29.0, 5.0, 61.0), poreVisibility);
        sample.albedo = baseColor * lerp(0.74h, 1.08h, grain)
            * lerp(1.0h, 0.78h, knot);
        sample.smoothness = 0.22h + grain * 0.1h - (half)poreRoughness * 0.08h;
        sample.smoothness = RbxCompensateUnresolvedRoughness(sample.smoothness,
            poreVisibility, 0.04h);
        sample.occlusion = 0.84h + grain * 0.15h;
        sample.height = (grain - 0.5) * 0.003 - knot * 0.001
            + (poreRoughness - 0.5) * 0.0004;
    }
    else if (materialMode == 3)
    {
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float seam;
        float grain;
        float plankTone;
        RbxBlendWoodPlankPattern(position, weights, seam, grain, plankTone);
        float fibreVisibility;
        float fibreRoughness = RbxFilteredValueNoise(
            physicalPosition * 240.0 + float3(37.0, 19.0, 73.0), fibreVisibility);
        half boardSmoothness = 0.25h + (half)grain * 0.05h
            - (half)fibreRoughness * 0.1h;
        sample.albedo = lerp(baseColor * lerp(0.82h, 1.08h, grain) * plankTone,
            baseColor * 0.34h, seam);
        sample.smoothness = lerp(boardSmoothness, 0.07h, seam);
        sample.smoothness = RbxCompensateUnresolvedRoughness(sample.smoothness,
            fibreVisibility, 0.045h);
        sample.occlusion = lerp(0.96h, 0.52h, seam);
        sample.height = (1.0 - seam) * (0.002 + grain * 0.0005
            + (fibreRoughness - 0.5) * 0.0002);
    }
    else if (materialMode == 4)
    {
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float machining;
        float machiningVisibility;
        RbxBlendDirectionalMachiningPattern(position, weights, 260.0, machining,
            machiningVisibility);
        float metalVariation = RbxFbm(physicalPosition * 1.45
            + float3(19.0, 7.0, 31.0));
        float microVisibility;
        float microRoughness = RbxFilteredValueNoise(
            physicalPosition * 420.0 + float3(3.0, 17.0, 11.0), microVisibility);
        sample.albedo = baseColor * (0.98h + (half)(metalVariation - 0.5) * 0.06h);
        sample.metallic = 1.0h;
        sample.smoothness = 0.8h + (half)(metalVariation - 0.5) * 0.1h
            - (half)abs(machining - 0.5) * 0.22h
            - (half)(microRoughness - 0.5) * 0.06h;
        sample.smoothness = RbxCompensateUnresolvedRoughness(sample.smoothness,
            min(machiningVisibility, microVisibility), 0.1h);
        sample.occlusion = 1.0h;
        sample.height = (machining - 0.5) * 0.00045
            + (microRoughness - 0.5) * 0.00012;
    }
    else if (materialMode == 5)
    {
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float diamond;
        float treadEdge;
        RbxBlendDiamondPlatePattern(position, weights, diamond, treadEdge);
        float machining;
        float machiningVisibility;
        RbxBlendDirectionalMachiningPattern(position, weights, 8.0, machining,
            machiningVisibility);
        float wear = RbxFbm(physicalPosition * 1.4 + float3(31.0, 13.0, 47.0));
        float microVisibility;
        float microRoughness = RbxFilteredValueNoise(
            physicalPosition * 280.0 + float3(71.0, 23.0, 5.0), microVisibility);
        sample.albedo = baseColor * (0.98h + (half)(wear - 0.5) * 0.04h)
            * lerp(0.97h, 1.02h, diamond);
        sample.metallic = 1.0h;
        sample.smoothness = 0.62h + (half)diamond * 0.18h
            - (half)treadEdge * 0.16h - (half)abs(machining - 0.5) * 0.18h
            - (half)(microRoughness - 0.5) * 0.05h;
        sample.smoothness = RbxCompensateUnresolvedRoughness(sample.smoothness,
            min(machiningVisibility, microVisibility), 0.1h);
        sample.occlusion = lerp(0.94h, 1.0h, diamond) - (half)treadEdge * 0.08h;
        sample.height = diamond * 0.0018 + (machining - 0.5) * 0.00035;
    }
    else if (materialMode == 6)
    {
        float corrosion = smoothstep(0.43, 0.72, broadNoise + detailNoise * 0.2);
        float pitVisibility;
        float pitNoise = RbxFilteredValueNoise(
            physicalPosition * 80.0 + float3(13.0, 53.0, 29.0), pitVisibility);
        float pits = smoothstep(0.77, 0.93, pitNoise) * corrosion;
        half3 rustColor = baseColor * half3(1.12h, 0.42h, 0.12h);
        sample.albedo = lerp(baseColor * (0.58h + detailNoise * 0.3h), rustColor, corrosion);
        sample.albedo *= lerp(1.0h, 0.42h, pits);
        sample.metallic = lerp(0.9h, 0.04h, corrosion);
        sample.smoothness = lerp(0.56h, 0.13h, corrosion);
        sample.smoothness = RbxCompensateUnresolvedRoughness(sample.smoothness,
            pitVisibility, 0.05h);
        sample.occlusion = lerp(0.94h, 0.58h, pits);
        sample.height = (broadNoise - 0.5) * 0.005 - pits * 0.003;
    }
    else if (materialMode == 7)
    {
        float4 marbleField = RbxSimplexFbmGrad(position * 0.42 + float3(13.0, 29.0, 47.0));
        float ribbonCoordinate = dot(position, float3(0.78, 0.18, 0.42))
            + (marbleField.w - 0.5) * 2.7;
        float ribbonPhase = ribbonCoordinate * 2.15;
        float ribbonVisibility;
        float ribbonWave = abs((RbxFilteredWave(ribbonPhase, fwidth(ribbonPhase),
            ribbonVisibility) - 0.5) * 2.0);
        float veinHalo = 1.0 - smoothstep(0.12, 0.48, ribbonWave);
        float mainVein = 1.0 - smoothstep(0.035, 0.2, ribbonWave);
        float branchCoordinate = dot(position, float3(-0.24, 0.62, 0.51))
            + marbleField.w * 1.35;
        float branchPhase = branchCoordinate * 1.45 + 1.2;
        float branchVisibility;
        float branchWave = abs((RbxFilteredWave(branchPhase, fwidth(branchPhase),
            branchVisibility) - 0.5) * 2.0);
        float branchVein = (1.0 - smoothstep(0.055, 0.24, branchWave))
            * smoothstep(0.52, 0.78, marbleField.w);
        float combinedVein = saturate(mainVein + branchVein * 0.42);
        half3 stoneColor = baseColor * (0.93h + marbleField.w * 0.12h);
        half3 haloColor = lerp(stoneColor, baseColor * 0.69h, (half)veinHalo * 0.58h);
        half3 veinColor = lerp(baseColor * 0.42h, half3(0.31h, 0.29h, 0.27h), 0.28h);
        sample.albedo = lerp(haloColor, veinColor, combinedVein);
        sample.smoothness = 0.72h - combinedVein * 0.11h;
        sample.occlusion = 0.97h;
        sample.height = -combinedVein * 0.0015 - veinHalo * 0.00035;
        sample.heightGradient = marbleField.xyz * (0.42 * 0.0008);
    }
    else if (materialMode == 8)
    {
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float strata = RbxBlendSlatePattern(position, weights, broadNoise);
        float split = smoothstep(0.82, 0.96, strata);
        sample.albedo = baseColor * (0.48h + broadNoise * 0.48h) * lerp(1.0h, 0.48h, split);
        sample.smoothness = 0.22h + broadNoise * 0.1h;
        sample.occlusion = lerp(0.92h, 0.64h, split);
        sample.height = (strata - 0.5) * 0.003 - split * 0.004;
    }
    else if (materialMode == 9)
    {
        float aggregateVisibility;
        float aggregate = RbxFilteredValueNoise(
            physicalPosition * 28.0 + float3(7.0, 41.0, 17.0), aggregateVisibility);
        float pitVisibility;
        float pitNoise = RbxFilteredValueNoise(
            physicalPosition * 90.0 + float3(47.0, 3.0, 67.0), pitVisibility);
        float pits = smoothstep(0.84, 0.96, pitNoise);
        float flecks = smoothstep(0.78, 0.9, aggregate);
        float poreVisibility;
        float poreRoughness = RbxFilteredValueNoise(
            physicalPosition * 220.0 + float3(73.0, 31.0, 11.0), poreVisibility);
        sample.albedo = baseColor * (0.94h + (half)(broadNoise - 0.5) * 0.14h);
        sample.albedo = lerp(sample.albedo, baseColor * 1.04h, flecks * 0.24);
        sample.albedo *= lerp(1.0h, 0.78h, pits);
        sample.smoothness = 0.22h + (half)flecks * 0.06h - (half)pits * 0.08h
            - (half)poreRoughness * 0.08h;
        sample.smoothness = RbxCompensateUnresolvedRoughness(sample.smoothness,
            min(min(aggregateVisibility, pitVisibility), poreVisibility), 0.055h);
        sample.occlusion = lerp(0.92h, 0.5h, pits);
        sample.height = (broadNoise - 0.5) * 0.003 + (aggregate - 0.5) * 0.001
            - pits * 0.004
            + (poreRoughness - 0.5) * 0.0008;
    }
    else if (materialMode == 10)
    {
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float mortar;
        float brickTone;
        RbxBlendBrickPattern(position, weights, mortar, brickTone);
        float poreVisibility;
        float poreRoughness = RbxFilteredValueNoise(
            physicalPosition * 180.0 + float3(11.0, 59.0, 37.0), poreVisibility);
        half3 brickColor = baseColor * brickTone
            * (0.98h + (half)(detailNoise - 0.5) * 0.08h);
        half3 mortarColor = lerp(baseColor * 0.52h, half3(0.48h, 0.46h, 0.42h), 0.42h);
        sample.albedo = lerp(brickColor, mortarColor, mortar);
        half brickSmoothness = 0.27h - (half)poreRoughness * 0.1h;
        sample.smoothness = lerp(brickSmoothness, 0.07h, mortar);
        sample.smoothness = RbxCompensateUnresolvedRoughness(sample.smoothness,
            poreVisibility, 0.05h);
        sample.occlusion = lerp(0.96h, 0.6h, mortar);
        sample.height = (1.0 - mortar) * (0.006 + (broadNoise - 0.5) * 0.0015
            + (poreRoughness - 0.5) * 0.0005);
    }
    else if (materialMode == 11)
    {
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float stoneMask;
        float stoneDome;
        float stoneTone;
        RbxBlendCobblestonePattern(position, weights, stoneMask, stoneDome, stoneTone);
        float mineralVisibility;
        float mineralRoughness = RbxFilteredValueNoise(
            physicalPosition * 140.0 + float3(43.0, 7.0, 83.0), mineralVisibility);
        half3 jointColor = baseColor * 0.44h;
        half3 stoneColor = baseColor * stoneTone
            * (0.98h + (half)(broadNoise - 0.5) * 0.12h);
        sample.albedo = lerp(jointColor, stoneColor, stoneMask);
        half stoneSmoothness = 0.14h + (half)stoneDome * 0.12h
            - (half)mineralRoughness * 0.08h;
        sample.smoothness = lerp(0.045h, stoneSmoothness, stoneMask);
        sample.smoothness = RbxCompensateUnresolvedRoughness(sample.smoothness,
            mineralVisibility, 0.05h);
        sample.occlusion = lerp(0.46h, 0.97h, stoneMask);
        sample.height = stoneDome * 0.018
            + stoneMask * (mineralRoughness - 0.5) * 0.0006;
    }
    else if (materialMode == 12)
    {
        float4 grassField = RbxSimplexFbmGrad(position * 0.1
            + float3(5.0, 17.0, 31.0));
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float bladeMask;
        float bladeRidge;
        float bladeTone;
        RbxBlendGrassPattern(position, weights, grassField.w, bladeMask, bladeRidge, bladeTone);
        float clumps = saturate(grassField.w * 0.78 + broadNoise * 0.22);
        half3 thatchColor = baseColor * (0.54h + clumps * 0.08h);
        half3 bladeColor = baseColor * (0.9h + clumps * 0.12h + bladeTone * 0.08h);
        sample.albedo = lerp(thatchColor, bladeColor, bladeMask);
        sample.albedo = lerp(sample.albedo, baseColor * 1.05h, bladeRidge * 0.2);
        sample.smoothness = 0.11h + bladeMask * 0.1h;
        sample.occlusion = lerp(0.57h, 0.98h, bladeMask);
        sample.height = bladeMask * (0.003 + bladeTone * 0.001) + bladeRidge * 0.0008;
        sample.heightGradient = grassField.xyz * (0.1 * 0.0003);
    }
    else if (materialMode == 13)
    {
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float ripple = RbxBlendSandPattern(position, weights, broadNoise);
        float grainVisibility;
        float grains = RbxFilteredValueNoise(
            physicalPosition * 320.0 + float3(5.0, 71.0, 29.0), grainVisibility);
        sample.albedo = baseColor * (0.91h + (half)ripple * 0.08h
            + (half)(grains - 0.5) * 0.04h);
        sample.smoothness = 0.11h + (half)ripple * 0.09h
            - (half)(grains - 0.5) * 0.08h;
        sample.smoothness = RbxCompensateUnresolvedRoughness(sample.smoothness,
            grainVisibility, 0.055h);
        sample.occlusion = 0.88h + ripple * 0.1h;
        sample.height = (ripple - 0.5) * 0.006 + (grains - 0.5) * 0.0003;
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
            * (0.9h + (half)(earthField.w - 0.5) * 0.12h
                + (half)(plateTone - 0.5) * 0.08h);
        half3 crackColor = baseColor * 0.42h;
        half3 stoneColor = lerp(baseColor * 1.04h, half3(0.3h, 0.28h, 0.24h), 0.24h)
            * (0.94h + (half)(pebbleTone - 0.5) * 0.1h);
        sample.albedo = lerp(earthColor, crackColor, cracks);
        sample.albedo = lerp(sample.albedo, stoneColor, pebble);
        sample.smoothness = lerp(0.13h, 0.055h, cracks);
        sample.smoothness = lerp(sample.smoothness, 0.2h, pebble);
        sample.occlusion = lerp(0.93h, 0.48h, cracks);
        sample.occlusion = lerp(sample.occlusion, 0.98h, pebble);
        sample.height = -cracks * 0.008 + pebble * (0.009 + pebbleTone * 0.003);
        sample.heightGradient = earthField.xyz * (0.5 * 0.0003);
    }
    else if (materialMode == 15)
    {
        float ridges = RbxFbm(position * 0.48 + broadNoise * 1.4);
        float mineral = smoothstep(0.62, 0.84, detailNoise);
        sample.albedo = baseColor * (0.72h + ridges * 0.32h);
        sample.albedo = lerp(sample.albedo, baseColor * 1.08h, mineral * 0.2);
        sample.smoothness = 0.16h + mineral * 0.08h;
        sample.occlusion = 0.62h + ridges * 0.36h;
        sample.height = (ridges - 0.5) * 0.04 + (detailNoise - 0.5) * 0.004;
    }
    else if (materialMode == 16)
    {
        float crystalVisibility;
        float crystalNoise = RbxFilteredValueNoise(
            physicalPosition * 180.0 + float3(31.0, 47.0, 7.0), crystalVisibility);
        float crystals = pow(max(crystalNoise, 0.0), 2.4);
        float drifts = RbxFbm(physicalPosition * 2.0);
        half blueShadow = (half)(1.0 - drifts) * 0.08h;
        sample.albedo = baseColor * (0.91h + crystals * 0.13h) + half3(0.0h, blueShadow * 0.5h, blueShadow);
        sample.smoothness = 0.34h + crystals * 0.16h;
        sample.smoothness = RbxCompensateUnresolvedRoughness(sample.smoothness,
            crystalVisibility, 0.08h);
        sample.occlusion = 0.9h + drifts * 0.1h;
        sample.height = (drifts - 0.5) * 0.01 + (crystals - 0.5) * 0.001;
    }
    else if (materialMode == 17)
    {
        float3 weights = RbxNarrowAxisWeights(patternNormal);
        float weave;
        float weaveVisibility;
        RbxBlendFabricPattern(position, weights, weave, weaveVisibility);
        float fibreVisibility;
        float fibreRoughness = RbxFilteredValueNoise(
            physicalPosition * 650.0 + float3(61.0, 17.0, 43.0), fibreVisibility);
        sample.albedo = baseColor * (0.97h + (half)(weave - 0.5) * 0.1h);
        sample.smoothness = 0.2h + (half)(weave - 0.5) * 0.08h
            - (half)(fibreRoughness - 0.5) * 0.12h;
        sample.smoothness = RbxCompensateUnresolvedRoughness(sample.smoothness,
            min(weaveVisibility, fibreVisibility), 0.07h);
        sample.occlusion = 0.82h + weave * 0.16h;
        sample.height = (weave - 0.5) * 0.00065
            + (fibreRoughness - 0.5) * 0.00018;
    }
    else
    {
        float invalidPattern = step(0.5, frac(dot(position, float3(1.0, 0.73, 1.31)) * 2.0));
        sample.albedo = lerp(half3(0.03h, 0.0h, 0.03h), half3(1.0h, 0.0h, 0.8h), invalidPattern);
        sample.smoothness = 0.15h;
        sample.occlusion = 1.0h;
        sample.height = invalidPattern * 0.2;
    }

    sample.metallic = saturate(sample.metallic);
    sample.smoothness = saturate(sample.smoothness);
    sample.occlusion = saturate(sample.occlusion);
    if (sample.metallic < 0.5h)
    {
        sample.albedo = clamp(sample.albedo, half3(0.02h, 0.02h, 0.02h),
            half3(0.9h, 0.9h, 0.9h));
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
    float heightFootprint = abs(heightDerivativeX) + abs(heightDerivativeY);
    float normalVisibility = 1.0 - smoothstep(0.55, 1.1, heightFootprint);
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
    return normalize(normalWS - (screenGradient + analyticalGradient)
        * (bumpStrength * normalVisibility));
}

#endif
