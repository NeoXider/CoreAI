Shader "CoreAI/Look/Horizon Fog"
{
    Properties
    {
        _OpaqueUpTo("Fully Fog Up To (sine of the elevation)", Float) = 0.035
        _ClearFrom("Clear From (sine of the elevation)", Float) = 0.17
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent-50"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "HorizonFog"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex HorizonVertex
            #pragma fragment HorizonFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _OpaqueUpTo;
                float _ClearFrom;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 direction : TEXCOORD0;
            };

            // WHY a dome at the far plane round every camera: the sky is drawn before the scene and URP's fog never
            // reaches it, so a world fogged to its edge stood out as white silhouettes on the sky's own horizon. The
            // dome sits just inside the far plane, behind everything the camera draws, and paints the fog colour
            // (whoever owns the fog: the look's haze or a world's Lighting.FogColor) below the horizon and in a band
            // above it, so fully fogged geometry and the sky behind it are one colour.
            Varyings HorizonVertex(Attributes input)
            {
                Varyings output;
                float3 direction = normalize(input.positionOS.xyz);
                float3 positionWS = _WorldSpaceCameraPos + direction * (_ProjectionParams.z * 0.95);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.direction = direction;
                return output;
            }

            half4 HorizonFragment(Varyings input) : SV_Target
            {
                float elevation = normalize(input.direction).y;
                half alpha = 1.0h - (half)smoothstep(_OpaqueUpTo, _ClearFrom, elevation);
                return half4(unity_FogColor.rgb, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
