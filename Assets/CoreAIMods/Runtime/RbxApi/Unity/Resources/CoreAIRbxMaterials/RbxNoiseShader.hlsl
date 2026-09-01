#ifndef COREAI_RBX_NOISE_SHADER_INCLUDED
#define COREAI_RBX_NOISE_SHADER_INCLUDED

#include "NoiseShader/Common.hlsl"

// WHY: NoiseShader's package-root include cannot resolve from a vendored Resources folder, so the namespaced adapter keeps the upstream kernel byte-identical and localizes the path/name changes here.
float4 RbxSimplexNoiseGrad(float3 value)
{
    float3 cell = floor(value + dot(value, 1.0 / 3.0));
    float3 corner0 = value - cell + dot(cell, 1.0 / 6.0);
    float3 order = corner0.yzx <= corner0.xyz;
    float3 inverseOrder = 1.0 - order;
    float3 offset1 = min(order.xyz, inverseOrder.zxy);
    float3 offset2 = max(order.xyz, inverseOrder.zxy);
    float3 corner1 = corner0 - offset1 + 1.0 / 6.0;
    float3 corner2 = corner0 - offset2 + 1.0 / 3.0;
    float3 corner3 = corner0 - 0.5;

    cell = wglnoise_mod289(cell);
    float4 permutation = wglnoise_permute(
        cell.z + float4(0.0, offset1.z, offset2.z, 1.0));
    permutation = wglnoise_permute(
        permutation + cell.y + float4(0.0, offset1.y, offset2.y, 1.0));
    permutation = wglnoise_permute(
        permutation + cell.x + float4(0.0, offset1.x, offset2.x, 1.0));

    float4 gradientX = lerp(-1.0, 1.0, frac(permutation / 7.0));
    float4 gradientY = lerp(-1.0, 1.0, frac(floor(permutation / 7.0) / 7.0));
    float4 gradientZ = 1.0 - abs(gradientX) - abs(gradientY);
    bool4 negativeZ = gradientZ < 0.0;
    gradientX += negativeZ * (gradientX < 0.0 ? 1.0 : -1.0);
    gradientY += negativeZ * (gradientY < 0.0 ? 1.0 : -1.0);

    float3 gradient0 = normalize(float3(gradientX.x, gradientY.x, gradientZ.x));
    float3 gradient1 = normalize(float3(gradientX.y, gradientY.y, gradientZ.y));
    float3 gradient2 = normalize(float3(gradientX.z, gradientY.z, gradientZ.z));
    float3 gradient3 = normalize(float3(gradientX.w, gradientY.w, gradientZ.w));
    float4 radial = float4(dot(corner0, corner0), dot(corner1, corner1),
        dot(corner2, corner2), dot(corner3, corner3));
    float4 projected = float4(dot(gradient0, corner0), dot(gradient1, corner1),
        dot(gradient2, corner2), dot(gradient3, corner3));
    radial = max(0.5 - radial, 0.0);
    float4 radialCubed = radial * radial * radial;
    float4 radialFourth = radial * radialCubed;
    float4 radialDerivative = -8.0 * radialCubed * projected;
    float3 analyticalGradient = radialFourth.x * gradient0 + radialDerivative.x * corner0
        + radialFourth.y * gradient1 + radialDerivative.y * corner1
        + radialFourth.z * gradient2 + radialDerivative.z * corner2
        + radialFourth.w * gradient3 + radialDerivative.w * corner3;
    return 107.0 * float4(analyticalGradient, dot(radialFourth, projected));
}

float4 RbxSimplexNoise01Grad(float3 position)
{
    float4 signedSample = RbxSimplexNoiseGrad(position);
    return float4(signedSample.xyz * 0.5, signedSample.w * 0.5 + 0.5);
}

float4 RbxSimplexFbmGrad(float3 position)
{
    float frequency = 1.0;
    float4 octave = RbxSimplexNoise01Grad(position);
    float3 gradient = octave.xyz * 0.5333;
    float value = octave.w * 0.5333;

    position = position * 2.03 + 17.17;
    frequency *= 2.03;
    octave = RbxSimplexNoise01Grad(position);
    gradient += octave.xyz * (0.2667 * frequency);
    value += octave.w * 0.2667;

    position = position * 2.01 + 9.23;
    frequency *= 2.01;
    octave = RbxSimplexNoise01Grad(position);
    gradient += octave.xyz * (0.1333 * frequency);
    value += octave.w * 0.1333;

    position = position * 2.04 + 5.71;
    frequency *= 2.04;
    octave = RbxSimplexNoise01Grad(position);
    gradient += octave.xyz * (0.0667 * frequency);
    value += octave.w * 0.0667;
    return float4(gradient, value);
}

#endif
