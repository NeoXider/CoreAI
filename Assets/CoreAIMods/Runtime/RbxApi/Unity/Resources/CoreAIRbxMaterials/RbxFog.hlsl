#ifndef RBX_FOG_INCLUDED
#define RBX_FOG_INCLUDED

// WHY: URP's ComputeFogFactor expects the clip-space z of the vertex stage. In the fragment stage
// SV_POSITION.z is device depth (reversed on D3D), which it maps to almost the far plane, so with fog on
// every Rbx surface beyond a couple of near-plane distances took the full fog colour. The fog distance is
// taken from the view depth instead, the way URP's own fragment fog (InitializeInputDataFog) measures it.
real RbxFogFactor(float3 positionWS)
{
    float viewZ = -TransformWorldToView(positionWS).z;
    return ComputeFogFactorZ0ToFar(max(viewZ - _ProjectionParams.y, 0.0));
}

#endif
