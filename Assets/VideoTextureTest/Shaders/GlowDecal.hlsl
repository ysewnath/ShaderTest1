// ----------------------------------------------------------------------------
// GlowDecal.hlsl
// Used by SG_GlowDecal.shadergraph (HDRP Decal, Custom Function "GlowDecal").
//
// Logo / text projected onto hardware as
// red-hot emissive light. Built from the logo's alpha/luminance:
//   - core   : the logo itself, white-yellow hot center
//   - glow   : mip-blurred logo (cheap wide blur), orange
//   - bleed  : very wide blur, deep red, spills onto surrounding metal
// plus heat flicker, heat shimmer, an optional "ignite" wipe (Reveal), an optional
// perforated-grille pattern (light shining through holes), and
// optional scorch darkening of the surface under the glow.
//
// The logo texture needs mipmaps (Unity's default) and a transparent/black margin
// around the artwork so the glow has room.
// ----------------------------------------------------------------------------
#ifndef VTT_GLOW_DECAL_INCLUDED
#define VTT_GLOW_DECAL_INCLUDED

float VTTGlow_Hash12(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float VTTGlow_Noise(float2 p)
{
    float2 i = floor(p), f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    float a = VTTGlow_Hash12(i), b = VTTGlow_Hash12(i + float2(1, 0));
    float c = VTTGlow_Hash12(i + float2(0, 1)), d = VTTGlow_Hash12(i + float2(1, 1));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

float VTTGlow_InverseExposure()
{
#if defined(SHADERGRAPH_PREVIEW)
    return 1.0;
#else
    return GetInverseCurrentExposureMultiplier();
#endif
}

// Logo coverage: luminance * alpha, so both white-on-transparent PNGs and
// white-on-black JPGs work.
float VTTGlow_Mask(UnityTexture2D tex, float2 uv, float lod)
{
    float4 c = SAMPLE_TEXTURE2D_LOD(tex.tex, tex.samplerstate, uv, lod);
    return saturate(dot(c.rgb, float3(0.299, 0.587, 0.114)) * c.a);
}

void GlowDecal_float(
    UnityTexture2D LogoTex,
    float2 UV,
    float4 LogoScaleOffset,
    float  Intensity,
    float4 CoreColor,
    float4 GlowColor,
    float4 BleedColor,
    float  GlowSize,         // mip level of the first blur (higher = wider glow)
    float  GlowStrength,
    float  BleedStrength,
    float  Flicker,
    float  FlickerSpeed,
    float  Shimmer,
    float  Reveal,           // 0..1 ignite wipe left -> right (1 = fully lit)
    float  RevealSoftness,
    float  Perforation,      // 0..1 how much the grille holes modulate the light
    float  PerforationScale,
    float  Scorch,           // 0..1 darken the surface under the glow
    float  ExposureWeight,
    out float3 Emission,
    out float3 BaseColor,
    out float  Alpha)
{
    float time = _TimeParameters.x;
    float2 uv = UV * LogoScaleOffset.xy + LogoScaleOffset.zw;

    // Heat shimmer: tiny animated UV wobble.
    float2 sh = float2(VTTGlow_Noise(uv * 18.0 + float2(0, time * 3.0)),
                       VTTGlow_Noise(uv * 18.0 + float2(time * 2.3, 7.7))) - 0.5;
    float2 uvS = uv + sh * Shimmer * 0.01;

    // Keep everything inside the projector box; fade gently at its edges.
    float2 inB = smoothstep(0.0, 0.04, uv) * smoothstep(0.0, 0.04, 1.0 - uv);
    float bounds = inB.x * inB.y;

    // Core (sharp) + two blur levels from the mip chain.
    // Core uses normal (derivative-based) sampling so it stays crisp up close and doesn't alias far away.
    float4 cs = SAMPLE_TEXTURE2D(LogoTex.tex, LogoTex.samplerstate, uvS);
    float core = saturate(dot(cs.rgb, float3(0.299, 0.587, 0.114)) * cs.a);
    float g1 = VTTGlow_Mask(LogoTex, uvS, GlowSize);
    float g2 = VTTGlow_Mask(LogoTex, uvS, GlowSize + 1.5);
    float glow = g1 * 0.6 + g2 * 0.4;
    float bleed = VTTGlow_Mask(LogoTex, uvS, GlowSize + 3.0);

    // Perforated grille: the light only gets through round holes.
    float2 cell = frac(uv * float2(PerforationScale * 4.0, PerforationScale)) - 0.5;
    float holes = 1.0 - smoothstep(0.26, 0.34, length(cell));
    float perf = lerp(1.0, holes, saturate(Perforation));

    // Ignite wipe with a white-hot leading edge.
    float s = max(RevealSoftness, 1e-3);
    float front = saturate(Reveal) * (1.0 + s) - s;
    float revealMask = 1.0 - smoothstep(front, front + s, uv.x);
    float burnEdge = exp(-pow((uv.x - (front + s * 0.5)) / (s * 0.5), 2.0)) * step(Reveal, 0.999);

    // Flicker (slow heat breathing + fast stutter).
    float flick = 1.0 + Flicker * ((VTTGlow_Noise(float2(time * FlickerSpeed, 3.1)) - 0.5) * 1.6
                                 + (VTTGlow_Hash12(float2(floor(time * 30.0), 1.7)) - 0.5) * 0.4);

    // Color: hot core blends toward white-yellow where the logo is solid.
    float3 coreCol = lerp(GlowColor.rgb, CoreColor.rgb, core * core);
    float3 e = coreCol * core * perf
             + GlowColor.rgb * glow * GlowStrength * lerp(1.0, perf, 0.5)
             + BleedColor.rgb * bleed * BleedStrength;
    e += CoreColor.rgb * burnEdge * saturate(core + glow) * 3.0;

    e *= Intensity * flick * revealMask * bounds;
    e = max(e, 0.0);

    // ExposureWeight 0 = constant on-screen brightness (intro look); 1 = physically exposed.
    Emission = lerp(e * VTTGlow_InverseExposure(), e, saturate(ExposureWeight));

    // Optional soot/scorch on the surface (affects albedo only where it glows).
    BaseColor = float3(0.02, 0.015, 0.012);
    Alpha = saturate(Scorch * saturate(glow * 1.5 + core) * revealMask * bounds);
}

void GlowDecal_half(
    UnityTexture2D LogoTex, half2 UV, half4 LogoScaleOffset, half Intensity, half4 CoreColor, half4 GlowColor,
    half4 BleedColor, half GlowSize, half GlowStrength, half BleedStrength, half Flicker, half FlickerSpeed,
    half Shimmer, half Reveal, half RevealSoftness, half Perforation, half PerforationScale, half Scorch,
    half ExposureWeight, out half3 Emission, out half3 BaseColor, out half Alpha)
{
    float3 e, bc; float a;
    GlowDecal_float(LogoTex, UV, LogoScaleOffset, Intensity, CoreColor, GlowColor, BleedColor, GlowSize,
        GlowStrength, BleedStrength, Flicker, FlickerSpeed, Shimmer, Reveal, RevealSoftness, Perforation,
        PerforationScale, Scorch, ExposureWeight, e, bc, a);
    Emission = (half3)e; BaseColor = (half3)bc; Alpha = (half)a;
}

#endif // VTT_GLOW_DECAL_INCLUDED
