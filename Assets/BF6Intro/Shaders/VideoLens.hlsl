// ----------------------------------------------------------------------------
// VideoLens.hlsl
// Used by SG_VideoLens.shadergraph (Custom Function node, File mode, name "VideoLens").
//
// Recreates the "footage living inside a glass capsule" look from the BF6 intro
// (~0:24-0:27): the video sits on a virtual plane *behind* the glass surface,
// is seen through a refracting lens (fisheye at the rim), gets chromatic
// fringing toward the edges, a dark vignette, a bright fresnel rim, and a
// subtle projector flicker / grain.
//
// Two mapping modes:
//   Mode 0  = Mesh UV      : flat screens / quads. Tangent-space parallax + barrel bulge.
//   Mode 1  = Lens Interior: any rounded mesh (sphere, capsule, egg). The video is
//                            projected on a plane inside the object, viewed through
//                            a refracted ray. The lens faces LensForward (object space),
//                            centered on LensCenter; VideoRenderTextureBinder can aim it
//                            at the camera every frame.
// ----------------------------------------------------------------------------
#ifndef BF6_VIDEO_LENS_INCLUDED
#define BF6_VIDEO_LENS_INCLUDED

float BF6_Hash12(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float BF6_InverseExposure()
{
#if defined(SHADERGRAPH_PREVIEW)
    return 1.0;
#else
    return GetInverseCurrentExposureMultiplier();
#endif
}

float3 BF6_SampleVideo(UnityTexture2D tex, float2 uv, float2 caDir)
{
    // LOD 0: video RTs have no mips, and this avoids derivative seams from the
    // per-pixel UV jumps that parallax / refraction introduce.
    float r = SAMPLE_TEXTURE2D_LOD(tex.tex, tex.samplerstate, uv + caDir, 0).r;
    float g = SAMPLE_TEXTURE2D_LOD(tex.tex, tex.samplerstate, uv,         0).g;
    float b = SAMPLE_TEXTURE2D_LOD(tex.tex, tex.samplerstate, uv - caDir, 0).b;
    return float3(r, g, b);
}

void VideoLens_float(
    UnityTexture2D VideoTex,
    float2 UV,
    float3 PositionOS,
    float3 NormalOS,
    float3 ViewDirOS,
    float3 ViewDirTS,
    float3 NormalWS,
    float3 ViewDirWS,
    float  Mode,
    float4 UVScaleOffset,
    float  Depth,
    float  IOR,
    float  ProjectionSize,
    float  Bulge,
    float  ChromaticAberration,
    float  Vignette,
    float4 Tint,
    float  Intensity,
    float  ExposureWeight,
    float  FresnelPower,
    float4 FresnelColor,
    float  Flicker,
    float  Grain,
    float  Reveal,
    float4 LensCenter,
    float4 LensForward,
    float4 LensUp,
    out float3 Emission,
    out float  Fresnel)
{
    // Build "lens space": the lens face looks along LensForward (object space),
    // LensUp keeps the footage upright. Default (0,0,-1)/(0,1,0) = identity.
    float3 lz = -normalize(LensForward.xyz + float3(0, 0, -1e-5));
    float3 upHint = LensUp.xyz;
    if (abs(dot(normalize(upHint + 1e-5), lz)) > 0.99) upHint = float3(1, 0, 0);
    float3 lx = normalize(cross(upHint, lz));
    float3 ly = cross(lz, lx);
    float3x3 toLens = float3x3(lx, ly, lz);

    float3 V   = normalize(mul(toLens, ViewDirOS));
    float3 N   = normalize(mul(toLens, NormalOS));
    float3 P   = mul(toLens, PositionOS - LensCenter.xyz);
    float2 uv;          // video UV before scale/offset
    float2 centered;    // -0.5..0.5 position used for vignette / CA direction
    float  frontMask = 1.0;

    if (Mode < 0.5)
    {
        // ---- Mode 0: mesh UV + tangent-space parallax + barrel bulge ----
        centered = UV - 0.5;
        float r2 = dot(centered, centered);
        float2 c = centered * (1.0 + Bulge * r2 * 4.0);          // fisheye squeeze at edges
        float3 vts = normalize(ViewDirTS);
        float2 parallax = vts.xy / max(vts.z, 0.15) * (Depth * 0.1);
        uv = c + 0.5 - parallax;
    }
    else
    {
        // ---- Mode 1: refracted ray onto a plane inside the object ----
        // Lens front faces local -Z; the video plane sits at z = +Depth.
        float3 R = refract(-V, N, 1.0 / max(IOR, 1.0001));
        if (dot(R, R) < 1e-4) R = -V;                             // total internal reflection fallback
        R.z = max(R.z, 0.05);                                     // ray must travel into the object
        float t = (Depth - P.z) / R.z;
        float3 hit = P + R * t;
        uv = hit.xy / max(ProjectionSize, 1e-3) + 0.5;
        centered = uv - 0.5;
        frontMask = smoothstep(0.0, 0.35, -N.z);                  // only the side facing -Z shows footage
    }

    // Crop / position the footage inside the lens.
    float2 videoUV = uv * UVScaleOffset.xy + UVScaleOffset.zw;

    // Fade out instead of clamping/streaking when the UV leaves the frame.
    float2 inside = smoothstep(0.0, 0.03, videoUV) * smoothstep(0.0, 0.03, 1.0 - videoUV);
    float  frameMask = inside.x * inside.y;

    // Chromatic aberration grows toward the rim.
    float  rad = length(centered) * 2.0;
    float2 caDir = centered * ChromaticAberration * (0.5 + rad * rad);
    float3 video = BF6_SampleVideo(VideoTex, saturate(videoUV), caDir);

    // Vignette (dark falloff toward the glass rim).
    float vig = 1.0 - Vignette * smoothstep(0.35, 1.1, rad);

    // Projector flicker (stepped at 24 fps) + per-pixel film grain.
    float time    = _TimeParameters.x;
    float frameId = floor(time * 24.0);
    float flick   = 1.0 + Flicker * (BF6_Hash12(float2(frameId, 7.13)) - 0.5) * 2.0;
    float grain   = (BF6_Hash12(UV * 1024.0 + frameId * 13.7) - 0.5) * Grain;

    // Fresnel: glass reflects more and lets less footage through at grazing angles.
    float NdotV = saturate(dot(normalize(NormalWS), normalize(ViewDirWS)));
    Fresnel = pow(1.0 - NdotV, max(FresnelPower, 0.01));

    // Power-on reveal: ramps brightness with a stuttering flicker near the start.
    float revealFlicker = lerp(step(0.35, BF6_Hash12(float2(frameId, 3.1))), 1.0, saturate(Reveal * 2.0 - 0.6));
    float reveal = saturate(Reveal) * revealFlicker;

    float3 footage = (video + grain) * Tint.rgb * Intensity;
    footage *= vig * frameMask * frontMask * flick * reveal * (1.0 - Fresnel * 0.85);

    float3 rim = FresnelColor.rgb * Fresnel;

    // ExposureWeight 0 = footage keeps its brightness regardless of scene exposure
    // (what you want for an intro); 1 = physically lit by the camera exposure.
    float3 hdr = max(footage + rim, 0.0);
    Emission = lerp(hdr * BF6_InverseExposure(), hdr, saturate(ExposureWeight));
}

// Half-precision entry point (Shader Graph picks _half when the node is set to Half).
void VideoLens_half(
    UnityTexture2D VideoTex, half2 UV, half3 PositionOS, half3 NormalOS, half3 ViewDirOS, half3 ViewDirTS,
    half3 NormalWS, half3 ViewDirWS, half Mode, half4 UVScaleOffset, half Depth, half IOR, half ProjectionSize,
    half Bulge, half ChromaticAberration, half Vignette, half4 Tint, half Intensity, half ExposureWeight,
    half FresnelPower, half4 FresnelColor, half Flicker, half Grain, half Reveal,
    half4 LensCenter, half4 LensForward, half4 LensUp,
    out half3 Emission, out half Fresnel)
{
    float3 e; float f;
    VideoLens_float(VideoTex, UV, PositionOS, NormalOS, ViewDirOS, ViewDirTS, NormalWS, ViewDirWS, Mode,
        UVScaleOffset, Depth, IOR, ProjectionSize, Bulge, ChromaticAberration, Vignette, Tint, Intensity,
        ExposureWeight, FresnelPower, FresnelColor, Flicker, Grain, Reveal, LensCenter, LensForward, LensUp, e, f);
    Emission = (half3)e;
    Fresnel  = (half)f;
}

#endif // BF6_VIDEO_LENS_INCLUDED
