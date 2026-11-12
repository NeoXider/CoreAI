// The Highlight adornment: one pass drawn twice over a part's own mesh. The fill draws the mesh tinted and marks its
// pixels in stencil bit 64; the outline draws the mesh grown by a few screen pixels where that bit is not set, which
// leaves a ring around the silhouette. Untagged, so URP draws it as SRPDefaultUnlit and the built-in pipeline as any
// unlit pass; no textures, no depth texture and no compute, so it runs on WebGL 2 as on desktop.
Shader "CoreAI/Rbx/Highlight"
{
    Properties
    {
        _Color("Color", Color) = (1, 0, 0, 0.5)
        _OutlinePixels("Outline Width (pixels)", Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest("Depth Test", Float) = 8
        [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp("Stencil Comparison", Float) = 8
        [Enum(UnityEngine.Rendering.StencilOp)] _StencilPass("Stencil Pass", Float) = 2
        _DepthOffset("Depth Offset", Float) = -1
    }

    SubShader
    {
        Tags { "Queue" = "Transparent+100" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Highlight"
            Cull Back
            ZWrite Off
            ZTest [_ZTest]
            Offset [_DepthOffset], [_DepthOffset]
            Blend SrcAlpha OneMinusSrcAlpha
            Stencil
            {
                Ref 64
                ReadMask 64
                WriteMask 64
                Comp [_StencilComp]
                Pass [_StencilPass]
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _OutlinePixels;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float4 local = v.vertex;
                if (_OutlinePixels > 0)
                {
                    // WHY grown per axis in object space: a part's mesh is a unit shape scaled to its Size, so scaling
                    // each axis by (1 + 2w / size) moves every face out by w world units, which keeps a box's ring
                    // closed at its corners (moving vertices along split normals would tear them apart).
                    float3 world = mul(unity_ObjectToWorld, local).xyz;
                    float distanceToCamera = max(length(_WorldSpaceCameraPos - world), 0.01);
                    float worldPerPixel = 2.0 * distanceToCamera / (abs(UNITY_MATRIX_P[1][1]) * _ScreenParams.y);
                    float width = _OutlinePixels * worldPerPixel;
                    float3 scale = float3(length(unity_ObjectToWorld._m00_m10_m20),
                        length(unity_ObjectToWorld._m01_m11_m21), length(unity_ObjectToWorld._m02_m12_m22));
                    local.xyz *= 1.0 + 2.0 * width / max(scale, 0.0001);
                }

                o.pos = UnityObjectToClipPos(local);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return _Color;
            }
            ENDCG
        }
    }
}
