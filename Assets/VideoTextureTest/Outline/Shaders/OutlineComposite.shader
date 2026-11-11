// Full-screen outline for OutlineHighlightPass.
// A pixel gets the outline when it is OUTSIDE the highlighted object and a nearby pixel shows a
// VISIBLE part of it. "Visible" compares the object's own depth with the scene depth (which includes
// the water surface) with a tolerance, so:
//  - parts below the water (or behind anything else) are never outlined,
//  - no line is drawn along the waterline (the object continues under the water there),
//  - nothing is drawn as extra geometry on the object, so there is no z-fighting / flicker with
//    the object or with effects that share its transform.
Shader "Hidden/VideoTextureTest/OutlineComposite"
{
    HLSLINCLUDE
    #pragma vertex Vert
    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch switch2

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"

    TEXTURE2D_X(_OutlineMask);
    // The camera's actual depth buffer. HDRP's usual depth texture (LoadCameraDepth) is a copy made
    // BEFORE the water is drawn, so it doesn't contain the water surface; this one does.
    TEXTURE2D_X_FLOAT(_OutlineSceneDepth);
    float4 _OutlineColor;
    float  _OutlineThickness;   // pixels at 1080p
    float  _OutlineOpacity;
    float  _DepthTolerance;     // relative eye-depth tolerance
    float  _OutlineIntensity;   // HDR multiplier: drawn before tonemapping, so >1 reads as clean white

    float SceneEyeDepth(int2 p)
    {
        return LinearEyeDepth(LOAD_TEXTURE2D_X(_OutlineSceneDepth, p).r, _ZBufferParams);
    }

    // True when the object covers p AND is not hidden there (by the water or anything else).
    bool VisibleObject(int2 p, out float objEye)
    {
        float2 m = LOAD_TEXTURE2D_X(_OutlineMask, p).rg;
        objEye = m.y;
        if (m.x < 0.5) return false;
        return m.y <= SceneEyeDepth(p) * (1.0 + _DepthTolerance) + 0.01;
    }

    float4 FullScreenPass(Varyings varyings) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);
        int2 p = int2(varyings.positionCS.xy);
        int2 maxP = int2(_ScreenSize.xy) - 1;

        // Inside the silhouette (visible or not, e.g. under the water): no outline.
        if (LOAD_TEXTURE2D_X(_OutlineMask, p).r > 0.5) return 0;

        float centerEye = SceneEyeDepth(p);
        float radius = max(_OutlineThickness * _ScreenSize.y / 1080.0, 1.0);

        // Two rings (full and half radius) + the direct neighbours.
        const int DIRS = 16;
        float hitsOuter = 0.0, hitsInner = 0.0;
        [unroll]
        for (int k = 0; k < DIRS; k++)
        {
            float a = k * (TWO_PI / DIRS);
            float2 d = float2(cos(a), sin(a));
            float objEye;

            int2 q = clamp(p + int2(round(d * radius)), 0, maxP);
            // Only if the object is in front of what this pixel shows (no outline over foreground / water in front).
            if (VisibleObject(q, objEye) && centerEye >= objEye * (1.0 - _DepthTolerance) - 0.01) hitsOuter += 1.0;

            int2 q2 = clamp(p + int2(round(d * max(radius * 0.5, 1.0))), 0, maxP);
            if (VisibleObject(q2, objEye) && centerEye >= objEye * (1.0 - _DepthTolerance) - 0.01) hitsInner += 1.0;
        }

        float alpha = hitsInner > 0.0 ? 1.0 : saturate(hitsOuter / 3.0);
        return float4(_OutlineColor.rgb * _OutlineIntensity, alpha * _OutlineOpacity * _OutlineColor.a);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "OutlineComposite"
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off

            HLSLPROGRAM
            #pragma fragment FullScreenPass
            ENDHLSL
        }
    }
    Fallback Off
}
