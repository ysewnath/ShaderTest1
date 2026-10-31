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
// Spin: JetEngineIgnition accumulates the fan angle (_SpinPhase) and its motion blur
// (_SpinBlur). Start: straight solid blades turning slowly anti-clockwise. End of the
// ignition: the fan reverses to clockwise, speeds up, and the blades twist into spirals.
// _Shimmer: heat wobble of fan / ring / cage (hub stays steady). _EffectTime drives the
// animation so it also moves in Edit mode.
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

float VTTJet_Hash21(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float VTTJet_Noise2(float2 x)
{
    float2 i = floor(x);
    float2 f = frac(x);
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(VTTJet_Hash21(i), VTTJet_Hash21(i + float2(1, 0)), u.x),
                lerp(VTTJet_Hash21(i + float2(0, 1)), VTTJet_Hash21(i + float2(1, 1)), u.x), u.y);
}

// Value noise that wraps every `period` cells in x (for seamless noise around the circle).
float VTTJet_NoiseP(float2 x, float period)
{
    float2 i = floor(x);
    float2 f = frac(x);
    float2 u = f * f * (3.0 - 2.0 * f);
    float i0 = i.x - period * floor(i.x / period);
    float i1 = (i.x + 1.0) - period * floor((i.x + 1.0) / period);
    return lerp(lerp(VTTJet_Hash21(float2(i0, i.y)),       VTTJet_Hash21(float2(i1, i.y)), u.x),
                lerp(VTTJet_Hash21(float2(i0, i.y + 1.0)), VTTJet_Hash21(float2(i1, i.y + 1.0)), u.x), u.y);
}

// Turbine blade mask at blade coordinate x (blade periods), lit from e0 to e1, soft edge bw.
float VTTJet_Blade(float x, float e0, float e1, float bw)
{
    float bf = frac(x);
    return smoothstep(e0 - bw, e0 + bw, bf) * (1.0 - smoothstep(e1 - bw, e1 + bw, bf));
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
    float  BladeTwist,     // blade twist at the start (0 = straight solid blades)
    float  HypnoTwist,     // blade twist at the end of the ignition (spiral "hypno" blades)
    float  HypnoWidth,     // blade width at the end of the ignition (0.46 = same as the start, up to ~0.85 = fat)
    float  SpinPhase,      // fan angle in turns, from JetEngineIgnition (+ = anti-clockwise on screen)
    float  SpinBlur,       // signed blades swept during the shutter (motion blur), from JetEngineIgnition
    float  SmearPhase,     // angle (turns) of the smears / hot side; can spin faster than the blades (they don't strobe)
    float  Shimmer,        // heat shimmer amount (hub stays undistorted)
    float  Imperfection,   // uneven, streaky, flickering smear on the fast-spinning fan (reads as speed)
    float  SegmentCount,
    float  SpokeCount,
    float  CageDarkness,
    float  Depth,          // parallax depth of the layers (in disc diameters)
    float  Flicker,
    float  Seed,
    float  EffectTime,     // seconds, from JetEngineIgnition (animates in Edit mode); 0 = shader time
    out float3 BaseColor,
    out float  Metallic,
    out float  Smoothness,
    out float3 Emission)
{
    float I = saturate(Ignition);
    float t = EffectTime > 0.0 ? EffectTime : _TimeParameters.x;

    // ---- ignition phases -------------------------------------------------
    float heat   = smoothstep(0.02, 0.30, I);          // turbine starts glowing
    float temp   = smoothstep(0.25, 0.90, I);          // colour temperature: magenta -> violet -> cyan
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
    // Heat shimmer: rolling warp from the blades outward. The hub / centre ring is left untouched.
    float2 sq  = UV * 6.0 + float2(0.0, -t * 1.7);
    float2 sq2 = UV * 13.0 + float2(t * 0.9, -t * 3.1);
    float2 warp = float2(VTTJet_Noise2(sq) - 0.5, VTTJet_Noise2(sq + 31.7) - 0.5)
                + 0.5 * float2(VTTJet_Noise2(sq2) - 0.5, VTTJet_Noise2(sq2 + 11.3) - 0.5);
    float rU = length((UV - 0.5) * 2.0);
    warp *= Shimmer * 0.035 * heat * smoothstep(0.32, 0.6, rU);

    float2 uvRing = UV + par * Depth * 0.15 + warp * 0.8;
    float2 uvFan  = UV + par * Depth * 0.45 + warp * 1.2;
    float2 uvHub  = UV + par * Depth * 0.80;
    float2 uvCage = UV + warp * 0.45;

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
    // Straight solid blades at the start; they wind into spiral "hypno" blades at the end of the ignition.
    float hypno  = smoothstep(0.55, 0.92, I);
    float twist  = lerp(BladeTwist, HypnoTwist, hypno);
    // Blades get fatter toward the end (narrow, dark gaps keep the motion readable through the glow).
    float width  = lerp(0.46, clamp(HypnoWidth, 0.2, 0.9), hypno);
    float e0     = 0.35 - width * 0.5;
    float e1     = 0.35 + width * 0.5;
    float bladeX = (aF + rF * twist * 0.15 + SpinPhase) * BladeCount;
    float bw     = BladeCount * dAF + 1e-3;
    // Motion blur: average over the angle swept while the shutter is open (signed: follows the spin direction).
    float sweep  = clamp(SpinBlur, -0.9, 0.9);       // capped so the blades never smear flat
    float blade  = 0.0;
    [unroll] for (int k = 0; k < 6; k++)
        blade += VTTJet_Blade(bladeX - sweep * (k / 5.0), e0, e1, bw + abs(sweep) * 0.06);
    blade /= 6.0;
    float fanBand = VTTJet_Band(rF, 0.28, 0.64, 0.02);
    float fan = fanBand * (0.12 + 0.88 * blade) * lerp(0.45, 1.0, smoothstep(0.3, 0.6, rF));
    fan *= 1.0 - hubMask;
    float fill = VTTJet_Band(rF, 0.26, 0.68, 0.06) * temp * (1.0 - 0.6 * blade) * (1.0 - 0.85 * hypno); // flame between the blades (gaps go dark at the end)

    // Imperfection (full power): a fast fan on film is never even. Each blade has its own brightness,
    // blotchy circular smears ride around with the fan, one side runs hotter and the smears flicker.
    // These features are large and move with the blades, so they read as fast rotation (no strobing).
    float imp = saturate(Imperfection) * hypno;
    float uRot = frac(aF + rF * twist * 0.15 + SmearPhase);             // angle that turns with the fan (full speed)
    float bladeId = floor(frac(bladeX / max(BladeCount, 1.0)) * max(BladeCount, 1.0));
    float bladeVar = VTTJet_Hash11(bladeId + 1.0 + Seed * 7.13);
    float smearA = VTTJet_NoiseP(float2(uRot * 7.0, rF * 16.0 + t * 0.6), 7.0);      // circular streaks
    float smearB = VTTJet_NoiseP(float2(uRot * 3.0, rF * 5.0 - t * 0.35), 3.0);      // big blotches
    float flick  = VTTJet_NoiseP(float2(uRot * 5.0, t * 9.0), 5.0);                  // fast flicker in the smears
    float hotSide = 0.5 + 0.5 * sin((uRot + 0.13 * sin(t * 0.7)) * VTTJET_TAU);      // one side brighter
    float uneven = (0.45 + 1.1 * bladeVar) * (0.35 + 1.3 * smearA) * (0.6 + 0.8 * smearB)
                 * (0.75 + 0.5 * hotSide) * (0.8 + 0.4 * flick);
    uneven = uneven * uneven * 0.55;                                  // more contrast, and below clipping so it survives bloom
    fan  *= lerp(1.0, uneven, imp);
    // Circular smear streaks laid over blades and gaps (motion-blurred hot gas / blade edges).
    float streak = smearA * smearA * smearA * (0.5 + smearB) * (0.7 + 0.6 * flick);
    fan += fanBand * (1.0 - hubMask) * imp * 0.9 * streak;
    // Streaky glow smeared across the gaps (the gaps aren't perfectly dark at speed).
    fill += VTTJet_Band(rF, 0.28, 0.66, 0.05) * temp * imp * 0.35 * smearA * smearA * (0.6 + 0.8 * smearB) * (1.0 - blade);

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
    ring *= lerp(1.0, 0.65 + 0.7 * hotSide, imp);                      // ring burns unevenly too
    float ringCore = saturate(1.0 - abs(rR - 0.78) / 0.09);
    float lines = exp(-pow((rR - 0.665) / max(0.006, px), 2.0)) + exp(-pow((rR - 0.895) / max(0.006, px), 2.0));

    // ---- cage in front -----------------------------------------------------
    float rC, aC, dAC;
    VTTJet_Polar(uvCage, rC, aC, dAC);
    float cx    = frac(aC * SpokeCount);
    float dLin  = abs(cx - 0.5) * (VTTJET_TAU / max(SpokeCount, 1.0)) * rC;  // distance to spoke centreline
    float spoke = 1.0 - smoothstep(0.022 - px, 0.022 + px, dLin);
    spoke *= VTTJet_Band(rC, 0.36, 1.05, 0.02);
    float cageRing = (1.0 - smoothstep(0.010 - px, 0.010 + px, abs(rC - 0.63)))
                   + (1.0 - smoothstep(0.018 - px, 0.018 + px, abs(rC - 0.95)));
    float cage = saturate(max(spoke, cageRing));

    float edge = 1.0 - smoothstep(0.94, 1.0, rU);      // fade into the nozzle wall (unwarped: rim stays steady)
    float inside = step(rU, 1.0);

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
    half Intensity, half ExposureWeight, half BladeCount, half BladeTwist, half HypnoTwist, half HypnoWidth,
    half SpinPhase, half SpinBlur, half SmearPhase, half Shimmer, half Imperfection,
    half SegmentCount, half SpokeCount, half CageDarkness, half Depth, half Flicker, half Seed, half EffectTime,
    out half3 BaseColor, out half Metallic, out half Smoothness, out half3 Emission)
{
    float3 b; float m; float s; float3 e;
    JetEngine_float(UV, ViewDirTS, Ignition, ColdColor, MidColor, HotColor, RingColor,
        Intensity, ExposureWeight, BladeCount, BladeTwist, HypnoTwist, HypnoWidth, SpinPhase, SpinBlur, SmearPhase, Shimmer, Imperfection,
        SegmentCount, SpokeCount, CageDarkness, Depth, Flicker, Seed, EffectTime, b, m, s, e);
    BaseColor = (half3)b; Metallic = (half)m; Smoothness = (half)s; Emission = (half3)e;
}

#endif // VTT_JET_ENGINE_INCLUDED
