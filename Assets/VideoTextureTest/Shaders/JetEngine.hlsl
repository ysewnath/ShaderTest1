// ----------------------------------------------------------------------------
// JetEngine.hlsl
// Used by SG_JetEngine.shadergraph (Custom Function node, File mode, name "JetEngine").
//
// Afterburner / turbine face seen from behind, with an ignition from off to on.
// Meant for a disc (or quad) sitting inside a nozzle, UV 0..1 across the opening.
// Layers, back to front (each pushed deeper with tangent-space parallax):
//   Hub      : dark center cone with a glowing seam.
//   Turbine  : twisted radial blades that glow first (magenta) and spin up.
//   Ring     : segmented outer ring; segments switch on one by one, then go white-hot.
//   Cage     : dark radial spokes + rings in front that block the glow.
//
// Ignition 0 = off (dark metal), ~0.15 first magenta glow, ~0.5 violet,
// 1 = full blue / cyan with white-hot ring. Animate _Ignition from Timeline
// (via JetEngineIgnition) or directly on the material.
// ----------------------------------------------------------------------------
#ifndef VTT_JET_ENGINE_INCLUDED
#define VTT_JET_ENGINE_INCLUDED

#define VTTJET_TAU 6.28318530718

float VTTJet_Hash11(float p)
{
    p = frac(p * 0.1031);
    p *= p + 33.33;
    p *= p + p;
    return frac(p);
}

float VTTJet_Noise1(float x)
{
    float i = floor(x);
    float f = frac(x);
    float u = f * f * (3.0 - 2.0 * f);
    return lerp(VTTJet_Hash11(i), VTTJet_Hash11(i + 1.0), u);
}

float VTTJet_InverseExposure()
{
#if defined(SHADERGRAPH_PREVIEW)
    return 1.0;
#else
    return GetInverseCurrentExposureMultiplier();
#endif
}

// Smooth band between r0 and r1.
float VTTJet_Band(float r, float r0, float r1, float soft)
{
    return smoothstep(r0 - soft, r0 + soft, r) * (1.0 - smoothstep(r1 - soft, r1 + soft, r));
}

// Polar coords of a UV (0..1 across the disc): r 0..1 at the rim, a01 0..1 around.
// dA = angular size of a pixel (in turns), computed without the atan2 seam.
void VTTJet_Polar(float2 uv, out float r, out float a01, out float dA)
{
    float2 p = (uv - 0.5) * 2.0;
    r   = length(p);
    a01 = atan2(p.y, p.x) / VTTJET_TAU + 0.5;
    dA  = length(fwidth(p)) / max(r, 1e-3) / VTTJET_TAU;
}

void JetEngine_float(
    float2 UV,
    float3 ViewDirTS,
    float  Ignition,
    float4 ColdColor,      // first glow (magenta)
    float4 MidColor,       // halfway (violet / blue)
    float4 HotColor,       // full power (cyan)
    float4 RingColor,      // white-hot core of the ring segments
    float  Intensity,
    float  ExposureWeight,
    float  BladeCount,
    float  BladeTwist,
    float  SpinSpeed,      // turns per second at full power
    float  SegmentCount,
    float  SpokeCount,
    float  CageDarkness,
    float  Depth,          // parallax depth of the layers (in disc diameters)
    float  Flicker,
    float  Seed,
    out float3 BaseColor,
    out float  Metallic,
    out float  Smoothness,
    out float3 Emission)
{
    float I = saturate(Ignition);
    float t = _TimeParameters.x;

    // ---- ignition phases -------------------------------------------------
    float heat   = smoothstep(0.02, 0.30, I);          // turbine starts glowing
    float temp   = smoothstep(0.25, 0.90, I);          // colour temperature: magenta -> violet -> cyan
    float spinUp = smoothstep(0.05, 0.85, I);
    float bright = heat * (0.4 + 0.6 * temp * temp);   // overall brightness

    float3 col = lerp(lerp(ColdColor.rgb, MidColor.rgb, saturate(temp * 2.0)),
                      HotColor.rgb, saturate(temp * 2.0 - 1.0));

    // Flicker: sputters while igniting, settles to a gentle shimmer at full power.
    float igniting = saturate(heat * (1.0 - temp) * 1.5);
    float fl = 1.0 + Flicker * (VTTJet_Noise1(t * 23.0 + Seed * 17.0) - 0.5) * 0.4;
    fl *= lerp(1.0, 0.45 + 1.1 * VTTJet_Noise1(t * 41.0 + Seed * 5.0), igniting * saturate(Flicker * 2.0));

    // ---- parallax: deeper layers shift against the view direction -------
    float3 V   = normalize(ViewDirTS);
    float2 par = -V.xy / max(V.z, 0.25) * 0.5;          // UV shift per disc-diameter of depth
    float2 uvRing = UV + par * Depth * 0.15;
    float2 uvFan  = UV + par * Depth * 0.45;
    float2 uvHub  = UV + par * Depth * 0.80;

    float px = length(fwidth(UV)) * 2.0 + 1e-4;         // pixel size in r units

    // ---- hub --------------------------------------------------------------
    float rH, aH, dAH;
    VTTJet_Polar(uvHub, rH, aH, dAH);
    float hubMask = 1.0 - smoothstep(0.24, 0.28, rH);
    float hubSeam = exp(-pow((rH - 0.268) / 0.012, 2.0)) * heat;
    float hubRings = 0.5 + 0.5 * sin(rH * 140.0);
    float hubGlow = hubMask * hubRings * 0.03 * temp;

    // ---- turbine blades ---------------------------------------------------
    float rF, aF, dAF;
    VTTJet_Polar(uvFan, rF, aF, dAF);
    float spin   = t * SpinSpeed * spinUp;
    float bladeX = (aF + rF * BladeTwist * 0.15 + spin) * BladeCount;
    float bf     = frac(bladeX);
    float bw     = BladeCount * dAF + 1e-3;
    float blade  = smoothstep(0.12 - bw, 0.12 + bw, bf) * (1.0 - smoothstep(0.58 - bw, 0.58 + bw, bf));
    blade = lerp(blade, 0.55, saturate(SpinSpeed * spinUp * 0.25 - 0.1));   // motion blur when spinning fast
    float fanBand = VTTJet_Band(rF, 0.28, 0.64, 0.02);
    float fan = fanBand * (0.12 + 0.88 * blade) * lerp(0.45, 1.0, smoothstep(0.3, 0.6, rF));
    fan *= 1.0 - hubMask;
    float fill = VTTJet_Band(rF, 0.26, 0.68, 0.06) * temp * (1.0 - 0.6 * blade); // flame glimpsed between the blades

    // ---- segmented outer ring --------------------------------------------
    float rR, aR, dAR;
    VTTJet_Polar(uvRing, rR, aR, dAR);
    float sx  = aR * SegmentCount + 0.25;
    float sid = floor(sx);
    float sf  = frac(sx);
    float sw  = SegmentCount * dAR + 1e-3;
    float seg = smoothstep(0.08 - sw, 0.08 + sw, sf) * (1.0 - smoothstep(0.92 - sw, 0.92 + sw, sf));
    float ringBand = VTTJet_Band(rR, 0.69, 0.87, max(0.01, px));
    float hsh   = VTTJet_Hash11(sid + 1.0 + Seed * 13.1);
    float onAt  = 0.10 + hsh * 0.35;                   // each segment has its own switch-on point
    float on    = smoothstep(onAt, onAt + 0.06, I);
    float segFl = lerp(1.0, VTTJet_Noise1(t * 35.0 + sid * 7.3), saturate(1.0 - abs(I - onAt - 0.03) * 10.0));
    float ring  = ringBand * seg * on * segFl;
    float ringCore = saturate(1.0 - abs(rR - 0.78) / 0.09);
    float lines = exp(-pow((rR - 0.665) / max(0.006, px), 2.0)) + exp(-pow((rR - 0.895) / max(0.006, px), 2.0));

    // ---- cage in front -----------------------------------------------------
    float rC, aC, dAC;
    VTTJet_Polar(UV, rC, aC, dAC);
    float cx    = frac(aC * SpokeCount);
    float dLin  = abs(cx - 0.5) * (VTTJET_TAU / max(SpokeCount, 1.0)) * rC;  // distance to spoke centreline
    float spoke = 1.0 - smoothstep(0.022 - px, 0.022 + px, dLin);
    spoke *= VTTJet_Band(rC, 0.36, 1.05, 0.02);
    float cageRing = (1.0 - smoothstep(0.010 - px, 0.010 + px, abs(rC - 0.63)))
                   + (1.0 - smoothstep(0.018 - px, 0.018 + px, abs(rC - 0.95)));
    float cage = saturate(max(spoke, cageRing));

    float edge = 1.0 - smoothstep(0.94, 1.0, rC);      // fade into the nozzle wall
    float inside = step(rC, 1.0);

    // ---- compose ------------------------------------------------------------
    float3 e = 0;
    e += col * fan * (0.7 + 0.9 * temp);
    e += HotColor.rgb * fill * 0.22 * (1.0 - hubMask);
    e += lerp(col, RingColor.rgb, saturate(temp * ringCore * ringCore)) * ring * (0.6 + 1.8 * temp);
    e += col * lines * heat * 0.35;
    e += col * hubSeam * 0.6 + HotColor.rgb * hubGlow;
    e *= 1.0 - cage * saturate(CageDarkness);
    e += col * cage * 0.04 * temp;                      // cage edges catch a little light
    e *= edge * inside * fl * bright * max(Intensity, 0.0);

    Emission = lerp(e * VTTJet_InverseExposure(), e, saturate(ExposureWeight));

    // Surface when off: dark metal cage/hub, black interior.
    BaseColor  = lerp(0.01, 0.05, cage) * inside;
    Metallic   = lerp(0.2, 0.8, cage);
    Smoothness = lerp(0.15, 0.45, cage);
}

void JetEngine_half(
    half2 UV, half3 ViewDirTS, half Ignition,
    half4 ColdColor, half4 MidColor, half4 HotColor, half4 RingColor,
    half Intensity, half ExposureWeight, half BladeCount, half BladeTwist, half SpinSpeed,
    half SegmentCount, half SpokeCount, half CageDarkness, half Depth, half Flicker, half Seed,
    out half3 BaseColor, out half Metallic, out half Smoothness, out half3 Emission)
{
    float3 b; float m; float s; float3 e;
    JetEngine_float(UV, ViewDirTS, Ignition, ColdColor, MidColor, HotColor, RingColor,
        Intensity, ExposureWeight, BladeCount, BladeTwist, SpinSpeed, SegmentCount, SpokeCount,
        CageDarkness, Depth, Flicker, Seed, b, m, s, e);
    BaseColor = (half3)b; Metallic = (half)m; Smoothness = (half)s; Emission = (half3)e;
}

#endif // VTT_JET_ENGINE_INCLUDED
