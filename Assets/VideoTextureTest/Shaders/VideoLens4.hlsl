// ----------------------------------------------------------------------------
// VideoLens4.hlsl  (version 4 - textured surface fading into the video)
// Used by SG_VideoLens4.shadergraph (Custom Function node, File mode, name "VideoLens4").
// Versions 1 and 3 are left untouched.
//
// v4 changes vs v3: outside the video window the mesh keeps its ORIGINAL HDRP Lit-style
// surface - Base Map x Base Color (with the base map's tiling/offset), Mask Map
// (R metallic, G AO, A smoothness) through Metallic/Smoothness/AO remapping, Normal Map
// with scale - instead of a flat color. "Keep Surface In Window" lets the surface show
// through under the footage. Also includes softer grazing-angle / frame-edge fades.
//
// v3 changes vs v2: outside the window the mesh keeps its original metallic look
// (Outside Color / Metallic / Smoothness, copied from the original material), and a
// wide soft mask fades that surface into dark glass + footage. Fresnel rim and edge
// ring only appear inside the window.
//
// v2 additions: the footage only shows inside an elliptical window on the front of
// the mesh (like a glass capsule, where the sides stay plain glass/metal). The window
// is fixed to the object (MaskForward/MaskUp, object space), independent of the
// camera-aimed lens, and has offset, stretch, rotation, softness, a glass edge ring,
// and separate base color / smoothness outside the window.
//
// Recreates a "footage living inside a glass capsule" look: the video sits on a virtual plane *behind* the glass surface,
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
#ifndef VTT_VIDEO_LENS4_INCLUDED
#define VTT_VIDEO_LENS4_INCLUDED

float VTTv4_Hash12(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float VTTv4_InverseExposure()
{
#if defined(SHADERGRAPH_PREVIEW)
    return 1.0;
#else
    return GetInverseCurrentExposureMultiplier();
#endif
}

float3 VTTv4_SampleVideo(UnityTexture2D tex, float2 uv, float2 caDir)
{
    // LOD 0: video RTs have no mips, and this avoids derivative seams from the
    // per-pixel UV jumps that parallax / refraction introduce.
    float r = SAMPLE_TEXTURE2D_LOD(tex.tex, tex.samplerstate, uv + caDir, 0).r;
    float g = SAMPLE_TEXTURE2D_LOD(tex.tex, tex.samplerstate, uv,         0).g;
    float b = SAMPLE_TEXTURE2D_LOD(tex.tex, tex.samplerstate, uv - caDir, 0).b;
    return float3(r, g, b);
}

void VideoLens4_float(
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
    float4 MaskForward,      // xyz: window direction (object space)
    float4 MaskUp,           // xyz: window up (object space)
    float  MeshRadius,       // object-space radius used to normalize the window
    float  MaskRadius,       // window size, 1 = mesh radius
    float4 MaskOffsetStretch,// xy: offset (mesh radii), zw: stretch
    float  MaskRotation,     // degrees
    float  MaskSoftness,     // 0..1 edge feather (fraction of the window)
    float  MaskFrontCutoff,  // 0..1: how far around the sides the window may wrap (0 = front only)
    float4 RingColor,        // HDR color of the glass edge ring
    float  RingWidth,        // ring width (fraction of window radius)
    float4 GlassColor,       // base color inside the window
    float  GlassSmoothness,
    float  GlassMetallic,
    UnityTexture2D BaseColorMap,   // surface outside the window (HDRP Lit-style)
    float4 BaseColorTint,
    UnityTexture2D MaskMap,        // R metallic, G AO, A smoothness
    UnityTexture2D NormalMap,
    float  NormalScale,
    float  UseMaskMap,             // 0: Metallic/Smoothness values, 1: Mask Map + remapping
    float  SurfaceMetallic,
    float  SurfaceSmoothness,
    float  MetallicRemapMin,
    float  MetallicRemapMax,
    float  SmoothnessRemapMin,
    float  SmoothnessRemapMax,
    float  AORemapMin,
    float  AORemapMax,
    float  KeepSurfaceInWindow,    // 0 = clean glass under the video, 1 = surface fully kept
    float  FadeCurve,        // >1 holds the outside surface longer, <1 shows video sooner
    float  VideoSpread,      // >1 lets the footage glow through the gold fade zone
    float  MaskDebug,        // 1 = show the mask in red/green
    out float3 Emission,
    out float  Fresnel,
    out float3 BaseColor,
    out float  Smoothness,
    out float  Metallic,
    out float  Occlusion,
    out float3 NormalTS,
    out float  Mask)
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
    float  grazeFade = 1.0;     // fades footage where the ray skims the glass (prevents smearing)
    float  planeFade = 1.0;     // round falloff on the virtual screen (no rectangular edge)

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
        grazeFade = smoothstep(0.08, 0.45, R.z);
        R.z = max(R.z, 0.05);                                     // ray must travel into the object
        float t = (Depth - P.z) / R.z;
        float3 hit = P + R * t;
        uv = hit.xy / max(ProjectionSize, 1e-3) + 0.5;
        centered = uv - 0.5;
        frontMask = smoothstep(-0.25, 0.35, -N.z);                // wide, soft falloff around the sides
        planeFade = 1.0 - smoothstep(0.3, 0.5, length(centered)); // circular edge on the virtual screen
    }

    // Crop / position the footage inside the lens.
    float2 videoUV = uv * UVScaleOffset.xy + UVScaleOffset.zw;

    // Fade out instead of clamping/streaking when the UV leaves the frame.
    float2 inside = smoothstep(0.0, 0.12, videoUV) * smoothstep(0.0, 0.12, 1.0 - videoUV);
    float  frameMask = inside.x * inside.y;

    // Chromatic aberration grows toward the rim.
    float  rad = length(centered) * 2.0;
    float2 caDir = centered * ChromaticAberration * (0.5 + rad * rad);
    float3 video = VTTv4_SampleVideo(VideoTex, saturate(videoUV), caDir);

    // Vignette (dark falloff toward the glass rim).
    float vig = 1.0 - Vignette * smoothstep(0.35, 1.1, rad);

    // Projector flicker (stepped at 24 fps) + per-pixel film grain.
    float time    = _TimeParameters.x;
    float frameId = floor(time * 24.0);
    float flick   = 1.0 + Flicker * (VTTv4_Hash12(float2(frameId, 7.13)) - 0.5) * 2.0;
    float grain   = (VTTv4_Hash12(UV * 1024.0 + frameId * 13.7) - 0.5) * Grain;

    // Fresnel: glass reflects more and lets less footage through at grazing angles.
    float NdotV = saturate(dot(normalize(NormalWS), normalize(ViewDirWS)));
    Fresnel = pow(1.0 - NdotV, max(FresnelPower, 0.01));

    // Power-on reveal: ramps brightness with a stuttering flicker near the start.
    float revealFlicker = lerp(step(0.35, VTTv4_Hash12(float2(frameId, 3.1))), 1.0, saturate(Reveal * 2.0 - 0.6));
    float reveal = saturate(Reveal) * revealFlicker;

    // ---- v2: object-fixed front window mask ----
    float3 mz = -normalize(MaskForward.xyz + float3(0, 0, -1e-5));
    float3 mUp = MaskUp.xyz;
    if (abs(dot(normalize(mUp + 1e-5), mz)) > 0.99) mUp = float3(1, 0, 0);
    float3 mx = normalize(cross(mUp, mz));
    float3 my = cross(mz, mx);
    float3x3 toMask = float3x3(mx, my, mz);
    float3 pm = mul(toMask, PositionOS - LensCenter.xyz) / max(MeshRadius, 1e-4);
    float3 nm = normalize(mul(toMask, NormalOS));

    float2 wxy = pm.xy - MaskOffsetStretch.xy;
    float sa, ca;
    sincos(radians(MaskRotation), sa, ca);
    wxy = float2(ca * wxy.x - sa * wxy.y, sa * wxy.x + ca * wxy.y);
    wxy /= max(abs(MaskOffsetStretch.zw), 1e-3);
    float d = length(wxy) / max(MaskRadius, 1e-3);                 // 1 = window edge

    float soft = max(MaskSoftness, 1e-3);
    float window = 1.0 - smoothstep(1.0 - soft, 1.0, d);
    // Keep the window on the front: facing(-1 front .. +1 back) must be below the cutoff.
    float facing = nm.z;
    float cutoff = lerp(-0.15, 0.95, saturate(MaskFrontCutoff));
    float front = 1.0 - smoothstep(cutoff - 0.2 - soft * 0.6, cutoff + 0.05, facing);
    Mask = pow(saturate(window * front), max(FadeCurve, 0.01));

    float ringW = max(RingWidth, 1e-4);
    float ring = exp(-pow((d - 1.0) / ringW, 2.0)) * front * step(1e-4, RingWidth);

    float3 footage = (video + grain) * Tint.rgb * Intensity;
    float videoMask = saturate(Mask * max(VideoSpread, 0.0));
    footage *= vig * frameMask * frontMask * grazeFade * planeFade * flick * reveal * (1.0 - Fresnel * 0.4) * videoMask;

    float3 rim = (FresnelColor.rgb * Fresnel * Mask + RingColor.rgb * ring) * saturate(Reveal);

    // ---- v4: original surface (HDRP Lit-style), tiled with the base map's tiling/offset ----
    float2 suv = UV * BaseColorMap.scaleTranslate.xy + BaseColorMap.scaleTranslate.zw;
    float4 baseS = SAMPLE_TEXTURE2D(BaseColorMap.tex, BaseColorMap.samplerstate, suv) * BaseColorTint;
    float4 maskS = SAMPLE_TEXTURE2D(MaskMap.tex, MaskMap.samplerstate, suv);
    float  surfMetal  = UseMaskMap > 0.5 ? lerp(MetallicRemapMin, MetallicRemapMax, maskS.r) : SurfaceMetallic;
    float  surfSmooth = UseMaskMap > 0.5 ? lerp(SmoothnessRemapMin, SmoothnessRemapMax, maskS.a) : SurfaceSmoothness;
    float  surfAO     = UseMaskMap > 0.5 ? lerp(AORemapMin, AORemapMax, maskS.g) : 1.0;
    float3 surfN      = UnpackNormalScale(SAMPLE_TEXTURE2D(NormalMap.tex, NormalMap.samplerstate, suv), NormalScale);

    // Inside the window the surface turns into dark glass, keeping some of it if asked.
    float g = Mask * (1.0 - saturate(KeepSurfaceInWindow));
    BaseColor  = lerp(baseS.rgb, GlassColor.rgb, g);
    Smoothness = lerp(surfSmooth, GlassSmoothness, g);
    Metallic   = lerp(surfMetal, GlassMetallic, g);
    Occlusion  = lerp(surfAO, 1.0, g);
    NormalTS   = normalize(lerp(surfN, float3(0, 0, 1), g));
    if (MaskDebug > 0.5)
    {
        footage = float3(1.0 - Mask, Mask, 0.0) + ring;   // red = outside, green = video window
        rim = 0;
    }

    // ExposureWeight 0 = footage keeps its brightness regardless of scene exposure
    // (what you want for an intro); 1 = physically lit by the camera exposure.
    float3 hdr = max(footage + rim, 0.0);
    Emission = lerp(hdr * VTTv4_InverseExposure(), hdr, saturate(ExposureWeight));
}

// Half-precision entry point (Shader Graph picks _half when the node is set to Half).
void VideoLens4_half(
    UnityTexture2D VideoTex, half2 UV, half3 PositionOS, half3 NormalOS, half3 ViewDirOS, half3 ViewDirTS,
    half3 NormalWS, half3 ViewDirWS, half Mode, half4 UVScaleOffset, half Depth, half IOR, half ProjectionSize,
    half Bulge, half ChromaticAberration, half Vignette, half4 Tint, half Intensity, half ExposureWeight,
    half FresnelPower, half4 FresnelColor, half Flicker, half Grain, half Reveal,
    half4 LensCenter, half4 LensForward, half4 LensUp,
    half4 MaskForward, half4 MaskUp, half MeshRadius, half MaskRadius, half4 MaskOffsetStretch, half MaskRotation,
    half MaskSoftness, half MaskFrontCutoff, half4 RingColor, half RingWidth, half4 GlassColor,
    half GlassSmoothness, half GlassMetallic,
    UnityTexture2D BaseColorMap, half4 BaseColorTint, UnityTexture2D MaskMap, UnityTexture2D NormalMap, half NormalScale,
    half UseMaskMap, half SurfaceMetallic, half SurfaceSmoothness,
    half MetallicRemapMin, half MetallicRemapMax, half SmoothnessRemapMin, half SmoothnessRemapMax,
    half AORemapMin, half AORemapMax, half KeepSurfaceInWindow,
    half FadeCurve, half VideoSpread, half MaskDebug,
    out half3 Emission, out half Fresnel, out half3 BaseColor, out half Smoothness, out half Metallic,
    out half Occlusion, out half3 NormalTS, out half Mask)
{
    float3 e; float f; float3 bc; float sm; float mt; float ao; float3 n; float mk;
    VideoLens4_float(VideoTex, UV, PositionOS, NormalOS, ViewDirOS, ViewDirTS, NormalWS, ViewDirWS, Mode,
        UVScaleOffset, Depth, IOR, ProjectionSize, Bulge, ChromaticAberration, Vignette, Tint, Intensity,
        ExposureWeight, FresnelPower, FresnelColor, Flicker, Grain, Reveal, LensCenter, LensForward, LensUp,
        MaskForward, MaskUp, MeshRadius, MaskRadius, MaskOffsetStretch, MaskRotation, MaskSoftness, MaskFrontCutoff,
        RingColor, RingWidth, GlassColor, GlassSmoothness, GlassMetallic,
        BaseColorMap, BaseColorTint, MaskMap, NormalMap, NormalScale, UseMaskMap, SurfaceMetallic, SurfaceSmoothness,
        MetallicRemapMin, MetallicRemapMax, SmoothnessRemapMin, SmoothnessRemapMax, AORemapMin, AORemapMax,
        KeepSurfaceInWindow, FadeCurve, VideoSpread, MaskDebug,
        e, f, bc, sm, mt, ao, n, mk);
    Emission = (half3)e; Fresnel = (half)f; BaseColor = (half3)bc; Smoothness = (half)sm;
    Metallic = (half)mt; Occlusion = (half)ao; NormalTS = (half3)n; Mask = (half)mk;
}

#endif // VTT_VIDEO_LENS4_INCLUDED
