// Draws highlighted objects into the outline mask: R = coverage, G = linear eye depth.
// Used by OutlineHighlightPass (never as a regular material).
Shader "Hidden/VideoTextureTest/OutlineMask"
{
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "OutlineMask"
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float  eyeDepth   : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.eyeDepth = -TransformWorldToView(positionWS).z;   // linear, same space as the camera depth
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                return float4(1.0, i.eyeDepth, 0.0, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
