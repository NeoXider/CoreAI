#ifndef COREAI_RBX_PROCEDURAL_COMMON_INCLUDED
#define COREAI_RBX_PROCEDURAL_COMMON_INCLUDED

struct RbxSurfaceSample
{
    half3 albedo;
    half metallic;
    half smoothness;
    half occlusion;
    float height;
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

float3 RbxTriplanarWeights(float3 normal)
{
    float3 weights = pow(saturate(abs(normal)), 4.0);
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

float2 RbxProjectedUv(float3 position, float3 normal)
{
    float3 absoluteNormal = abs(normal);
    if (absoluteNormal.y >= absoluteNormal.x && absoluteNormal.y >= absoluteNormal.z)
    {
        return position.xz;
    }

    if (absoluteNormal.x >= absoluteNormal.z)
    {
        return position.zy;
    }

    return position.xy;
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

float RbxGrassBladeLayer(float2 uv, float density, float seed)
{
    float2 gridPosition = uv * density;
    float2 cell = floor(gridPosition);
    float2 localPosition = frac(gridPosition) - 0.5;
    float2 jitter = float2(RbxHash31(float3(cell, seed)),
        RbxHash31(float3(cell, seed + 17.0))) - 0.5;
    localPosition -= jitter * 0.5;
    float angle = (RbxHash31(float3(cell, seed + 31.0)) - 0.5) * 1.7;
    float sine = sin(angle);
    float cosine = cos(angle);
    float2 bladePosition = float2(localPosition.x * cosine - localPosition.y * sine,
        localPosition.x * sine + localPosition.y * cosine);
    float halfWidth = 0.035 + RbxHash31(float3(cell, seed + 43.0)) * 0.035;
    float halfLength = 0.2 + RbxHash31(float3(cell, seed + 59.0)) * 0.18;
    float blade = 1.0 - smoothstep(halfWidth, halfWidth + 0.035, abs(bladePosition.x));
    blade *= 1.0 - smoothstep(halfLength, halfLength + 0.07, abs(bladePosition.y));
    return blade;
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
    float2 uv = RbxProjectedUv(position, patternNormal);
    float broadNoise = RbxFbm(position * 0.72);
    float detailNoise = RbxValueNoise(position * 5.3);
    sample.albedo = baseColor;
    sample.metallic = 0.0h;
    sample.smoothness = 0.4h;
    sample.occlusion = 1.0h;
    sample.height = 0.0;

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
        float2 uvX;
        float2 uvY;
        float2 uvZ;
        RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
        float seamX;
        float seamY;
        float seamZ;
        float grainX;
        float grainY;
        float grainZ;
        float toneX;
        float toneY;
        float toneZ;
        RbxWoodPlankPattern(uvX, 0.0, seamX, grainX, toneX);
        RbxWoodPlankPattern(uvY, 7.0, seamY, grainY, toneY);
        RbxWoodPlankPattern(uvZ, 13.0, seamZ, grainZ, toneZ);
        float3 weights = RbxTriplanarWeights(patternNormal);
        float seam = dot(weights, float3(seamX, seamY, seamZ));
        float grain = dot(weights, float3(grainX, grainY, grainZ));
        float plankTone = dot(weights, float3(toneX, toneY, toneZ));
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
        float2 plateCell = frac(uv * 1.35) - 0.5;
        float2 diamondCoordinates = float2(plateCell.x + plateCell.y, plateCell.x - plateCell.y);
        float diamond = 1.0 - smoothstep(0.22, 0.34,
            max(abs(diamondCoordinates.x), abs(diamondCoordinates.y)));
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
        float veinSignal = abs(sin(position.x * 2.5 + position.z * 0.8 + broadNoise * 7.0));
        float vein = pow(saturate(1.0 - veinSignal), 5.0);
        float fineVein = pow(saturate(1.0 - abs(sin(position.y * 4.0 + detailNoise * 5.0))), 10.0);
        float combinedVein = saturate(vein + fineVein * 0.45);
        sample.albedo = lerp(baseColor * (0.9h + broadNoise * 0.18h), baseColor * 0.18h,
            combinedVein);
        sample.smoothness = 0.68h - combinedVein * 0.16h;
        sample.occlusion = 0.97h;
        sample.height = broadNoise * 0.12 - combinedVein * 0.16;
    }
    else if (materialMode == 8)
    {
        float strata = 0.5 + 0.5 * sin((uv.y + broadNoise * 0.42) * 13.0);
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
        float2 uvX;
        float2 uvY;
        float2 uvZ;
        RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
        float mortarX;
        float mortarY;
        float mortarZ;
        float toneX;
        float toneY;
        float toneZ;
        RbxBrickPattern(uvX, 0.0, mortarX, toneX);
        RbxBrickPattern(uvY, 7.0, mortarY, toneY);
        RbxBrickPattern(uvZ, 13.0, mortarZ, toneZ);
        float3 weights = RbxTriplanarWeights(patternNormal);
        float mortar = dot(weights, float3(mortarX, mortarY, mortarZ));
        float brickTone = dot(weights, float3(toneX, toneY, toneZ));
        half3 brickColor = baseColor * brickTone * (0.83h + detailNoise * 0.23h);
        half3 mortarColor = lerp(baseColor * 0.34h, half3(0.42h, 0.4h, 0.36h), 0.52h);
        sample.albedo = lerp(brickColor, mortarColor, mortar);
        sample.smoothness = lerp(0.2h, 0.08h, mortar);
        sample.occlusion = lerp(0.96h, 0.6h, mortar);
        sample.height = (1.0 - mortar) * (0.68 + broadNoise * 0.16);
    }
    else if (materialMode == 11)
    {
        float2 uvX;
        float2 uvY;
        float2 uvZ;
        RbxPatternProjectionUvs(position, uvX, uvY, uvZ);
        float maskX;
        float maskY;
        float maskZ;
        float domeX;
        float domeY;
        float domeZ;
        float toneX;
        float toneY;
        float toneZ;
        RbxCobblestonePattern(uvX, 0.0, maskX, domeX, toneX);
        RbxCobblestonePattern(uvY, 7.0, maskY, domeY, toneY);
        RbxCobblestonePattern(uvZ, 13.0, maskZ, domeZ, toneZ);
        float3 weights = RbxTriplanarWeights(patternNormal);
        float stoneMask = dot(weights, float3(maskX, maskY, maskZ));
        float stoneDome = dot(weights, float3(domeX, domeY, domeZ));
        float stoneTone = dot(weights, float3(toneX, toneY, toneZ));
        half3 jointColor = baseColor * 0.2h;
        half3 stoneColor = baseColor * stoneTone * (0.9h + broadNoise * 0.18h);
        sample.albedo = lerp(jointColor, stoneColor, stoneMask);
        sample.smoothness = lerp(0.07h, 0.2h, stoneMask);
        sample.occlusion = lerp(0.46h, 0.97h, stoneMask);
        sample.height = stoneDome * 0.86;
    }
    else if (materialMode == 12)
    {
        float2 warp = float2(RbxValueNoise(position * 0.63),
            RbxValueNoise(position * 0.63 + float3(17.0, 31.0, 7.0))) - 0.5;
        float2 grassUv = uv + warp * 0.18;
        float bladeA = RbxGrassBladeLayer(grassUv, 5.7, 5.0);
        float2 rotatedUv = float2(grassUv.x * 0.819 - grassUv.y * 0.574,
            grassUv.x * 0.574 + grassUv.y * 0.819);
        float bladeB = RbxGrassBladeLayer(rotatedUv + float2(4.3, 7.1), 7.9, 71.0);
        float clumps = saturate(broadNoise * 0.7 + detailNoise * 0.3);
        float blades = saturate(bladeA + bladeB * 0.78);
        float grassTone = saturate(clumps * 0.54 + blades * 0.72);
        sample.albedo = baseColor * lerp(0.5h, 1.2h, grassTone);
        sample.smoothness = 0.16h + blades * 0.08h;
        sample.occlusion = 0.76h + grassTone * 0.22h;
        sample.height = blades * 0.68 + clumps * 0.2;
    }
    else if (materialMode == 13)
    {
        float ripple = 0.5 + 0.5 * sin(uv.x * 7.5 + sin(uv.y * 1.9) * 1.4 + broadNoise * 2.0);
        float grains = RbxValueNoise(position * 12.0);
        sample.albedo = baseColor * (0.72h + ripple * 0.22h + grains * 0.14h);
        sample.smoothness = 0.18h;
        sample.occlusion = 0.88h + ripple * 0.1h;
        sample.height = ripple * 0.56 + grains * 0.18;
    }
    else if (materialMode == 14)
    {
        float clods = smoothstep(0.34, 0.76, broadNoise);
        float grit = RbxValueNoise(position * 6.0);
        sample.albedo = baseColor * lerp(0.42h, 1.02h, clods) * (0.82h + grit * 0.18h);
        sample.smoothness = 0.12h + (1.0h - clods) * 0.07h;
        sample.occlusion = 0.68h + clods * 0.3h;
        sample.height = clods * 0.62 + grit * 0.22;
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
        float warp = 0.5 + 0.5 * sin(uv.x * 42.0);
        float weft = 0.5 + 0.5 * sin(uv.y * 42.0 + 1.5708);
        float weave = warp * 0.52 + weft * 0.48;
        sample.albedo = baseColor * (0.68h + weave * 0.42h);
        sample.smoothness = 0.2h;
        sample.occlusion = 0.82h + weave * 0.16h;
        sample.height = weave * 0.72;
    }
    else
    {
        float invalidPattern = step(0.5, frac((uv.x + uv.y) * 2.0));
        sample.albedo = lerp(half3(0.03h, 0.0h, 0.03h), half3(1.0h, 0.0h, 0.8h), invalidPattern);
        sample.smoothness = 0.15h;
        sample.occlusion = 1.0h;
        sample.height = invalidPattern * 0.2;
    }

    return sample;
}

float3 RbxPerturbNormal(float3 patternPosition, float3 patternNormal, float3 normalWS,
    bool objectAlignedProjection, int materialMode, float patternScale, float bumpStrength,
    half3 baseColor, float centerHeight)
{
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

    float epsilon = 0.025 / max(patternScale, 0.25);
    float tangentHeight = RbxEvaluateSurface(patternPosition + tangentPattern * epsilon,
        patternNormal, materialMode, patternScale, baseColor).height;
    float bitangentHeight = RbxEvaluateSurface(patternPosition + bitangentPattern * epsilon,
        patternNormal, materialMode, patternScale, baseColor).height;
    float tangentSlope = (tangentHeight - centerHeight) * bumpStrength / epsilon;
    float bitangentSlope = (bitangentHeight - centerHeight) * bumpStrength / epsilon;
    return normalize(normalWS - tangentWS * tangentSlope - bitangentWS * bitangentSlope);
}

#endif
