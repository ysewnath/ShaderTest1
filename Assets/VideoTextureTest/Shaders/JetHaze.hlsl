// ----------------------------------------------------------------------------
// JetHaze.hlsl
// Used by SG_JetHaze.shadergraph (Custom Function node, File mode, name "JetHaze").
//
// Exhaust heat haze: an invisible HDRP transparent surface that only writes
// screen-space distortion (HDRP Distortion pass), so whatever is behind it
// (engine glow, nozzle rim, floor) wobbles. Meant for the exhaust cone mesh:
// object space +Z = outward along the exhaust, radius 1 = nozzle radius.
// The centre (hub / centre ring) is left undistorted.
// _Ignition and _EffectTime are set by JetEngineIgnition.
// ----------------------------------------------------------------------------
#ifndef VTT_JET_HAZE_INCLUDED
#define VTT_JET_HAZE_INCLUDED

float VTTHaze_Hash31(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return frac((p.x + p.y) * p.z);
}

float VTTHaze_Noise3(float3 x)
{
    float3 i = floor(x);
    float3 f = frac(x);
    float3 u = f * f * (3.0 - 2.0 * f);
    float n000 = VTTHaze_Hash31(i);
    float n100 = VTTHaze_Hash31(i + float3(1, 0, 0));
    float n010 = VTTHaze_Hash31(i + float3(0, 1, 0));
    float n110 = VTTHaze_Hash31(i + float3(1, 1, 0));
    float n001 = VTTHaze_Hash31(i + float3(0, 0, 1));
    float n101 = VTTHaze_Hash31(i + float3(1, 0, 1));
    float n011 = VTTHaze_Hash31(i + float3(0, 1, 1));
    float n111 = VTTHaze_Hash31(i + float3(1, 1, 1));
    return lerp(lerp(lerp(n000, n100, u.x), lerp(n010, n110, u.x), u.y),
                lerp(lerp(n001, n101, u.x), lerp(n011, n111, u.x), u.y), u.z);
}

void JetHaze_float(
    float3 PositionOS,
    float3 NormalWS,
    float3 ViewDirWS,
    float  Ignition,
    float  Strength,      // screen-space distortion amount at full power
    float  NoiseScale,    // size of the heat cells (in nozzle radii)
    float  FlowSpeed,     // how fast the hot gas rushes out
    float  Blur,          // distortion blur at full power
    float  Length,        // plume length in nozzle radii (object space)
    float  EffectTime,    // seconds, from JetEngineIgnition (animates in Edit mode); 0 = shader time
    out float2 Distortion,
    out float  DistortionBlur,
    out float  Alpha)
{
    float t = EffectTime > 0.0 ? EffectTime : _TimeParameters.x;
    float heat = smoothstep(0.08, 0.75, saturate(Ignition));

    // Flow outward along +Z, swirling a little around the axis.
    float z = PositionOS.z;
    float ang = t * 0.6;
    float2 xy = PositionOS.xy;
    xy = float2(xy.x * cos(ang) - xy.y * sin(ang), xy.x * sin(ang) + xy.y * cos(ang));
    float3 p = float3(xy * NoiseScale, z * NoiseScale * 0.6 - t * FlowSpeed);

    float nx = VTTHaze_Noise3(p) + 0.5 * VTTHaze_Noise3(p * 2.03 + 17.1);
    float ny = VTTHaze_Noise3(p + 41.7) + 0.5 * VTTHaze_Noise3(p * 2.03 + 63.4);
    float2 n = (float2(nx, ny) / 1.5 - 0.5) * 2.0;

    // Strongest right at the nozzle, dying out along the plume.
    float along = saturate(z / max(Length, 1e-3));
    float fade = smoothstep(-0.2, 0.15, z) * (1.0 - smoothstep(0.0, 1.0, along));
    fade *= fade;
    // Keep the centre steady: no distortion over the hub / centre ring.
    fade *= smoothstep(0.35, 0.75, length(PositionOS.xy));
    // Hide the cone's silhouette edges: fade where it's seen edge-on.
    float facing = saturate(abs(dot(normalize(NormalWS), normalize(ViewDirWS))));
    fade *= smoothstep(0.05, 0.6, facing);

    Distortion = n * Strength * fade * heat;
    DistortionBlur = saturate(Blur * fade * heat);
    Alpha = 0.0;
}

void JetHaze_half(
    half3 PositionOS, half3 NormalWS, half3 ViewDirWS, half Ignition, half Strength, half NoiseScale,
    half FlowSpeed, half Blur, half Length, half EffectTime,
    out half2 Distortion, out half DistortionBlur, out half Alpha)
{
    float2 d; float b; float a;
    JetHaze_float(PositionOS, NormalWS, ViewDirWS, Ignition, Strength, NoiseScale, FlowSpeed, Blur, Length, EffectTime, d, b, a);
    Distortion = (half2)d; DistortionBlur = (half)b; Alpha = (half)a;
}

#endif // VTT_JET_HAZE_INCLUDED
