Shader "CoreAI/Rbx/Material Fallback"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry+20"
            "UniversalMaterialType" = "Unlit"
        }
        LOD 80

        Pass
        {
            Name "VisibleFallback"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex RbxFallbackVertex
            #pragma fragment RbxFallbackFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings RbxFallbackVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 RbxFallbackFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half stripe = step(0.5h, frac((input.positionWS.x + input.positionWS.y +
                    input.positionWS.z) * 2.4));
                half checker = step(0.5h, frac(input.positionWS.x * 4.0)) ==
                    step(0.5h, frac(input.positionWS.z * 4.0)) ? 1.0h : 0.0h;
                half pattern = saturate(stripe * 0.72h + checker * 0.28h);
                half3 viewDirectionWS = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half fresnel = pow(1.0h - saturate(dot(normalize(input.normalWS), viewDirectionWS)), 2.0h);
                half pulse = 0.86h + sin(_Time.y * 4.0) * 0.14h;
                half3 darkColor = half3(0.012h, 0.0h, 0.018h);
                half3 warningColor = half3(1.8h, 0.0h, 1.25h) * pulse;
                return half4(lerp(darkColor, warningColor, pattern) + fresnel * half3(0.35h, 0.0h, 0.3h),
                    1.0h);
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
    }

    Fallback "Hidden/InternalErrorShader"
}
