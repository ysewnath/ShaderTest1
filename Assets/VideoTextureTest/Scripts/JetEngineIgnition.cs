using System.Collections;
using UnityEngine;

namespace VideoTextureTest
{
    /// <summary>
    /// Drives the SG_JetEngine "_Ignition" (engine off -> magenta first light -> violet -> full blue/cyan)
    /// on one or more engine renderers, plus an optional light inside the nozzle that follows
    /// the shader's colour and brightness (it lights the nozzle walls and the floor).
    ///
    /// Auto: with Play On Start, the ignition plays by itself when entering Play mode.
    /// Timeline: turn Play On Start off and keyframe <see cref="ignition"/> with a regular
    /// Animation Track (it previews while scrubbing in Edit mode).
    /// Uses a MaterialPropertyBlock, so the material asset is never modified and several
    /// engines can share one material.
    /// </summary>
    [ExecuteAlways]
    public class JetEngineIgnition : MonoBehaviour
    {
        [Tooltip("Ignite automatically when entering Play mode. Turn OFF when a Timeline animates Ignition.")]
        public bool playOnStart = true;

        [Tooltip("0 = off, ~0.15 first magenta glow, ~0.5 violet, 1 = full power. Animatable from Timeline.")]
        [Range(0f, 1f)] public float ignition = 1f;

        [Header("Play On Start ignite")]
        [Min(0f)] public float delay = 0.5f;
        [Min(0.01f)] public float igniteDuration = 1.2f;
        public AnimationCurve igniteCurve = AnimationCurve.Linear(0, 0, 1, 1);
        public bool loop = false;
        [Min(0f)] public float holdTime = 2.5f;
        [Min(0f)] public float offTime = 1f;

        [Header("Targets")]
        [Tooltip("Renderers using SG_JetEngine (one per nozzle). Empty = all child renderers.")]
        public Renderer[] engineRenderers;
        [Tooltip("Optional light inside the nozzle; takes the shader's colour and brightness.")]
        public Light engineLight;
        [Min(0f)] public float lightMaxIntensity = 4f;
        [Range(0f, 1f)] public float lightFlicker = 0.15f;

        static readonly int IgnitionId = Shader.PropertyToID("_Ignition");
        static readonly int ColdId = Shader.PropertyToID("_ColdColor");
        static readonly int MidId  = Shader.PropertyToID("_MidColor");
        static readonly int HotId  = Shader.PropertyToID("_HotColor");

        MaterialPropertyBlock _mpb;

        void Start()
        {
            if (!Application.isPlaying) return;
            if (playOnStart) Play();
            else Apply();
        }

        public void Play()
        {
            StopAllCoroutines();
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            do
            {
                Set(0f);
                if (delay > 0f) yield return new WaitForSeconds(delay);
                for (float t = 0f; t < igniteDuration; t += Time.deltaTime)
                {
                    Set(igniteCurve.Evaluate(t / igniteDuration));
                    yield return null;
                }
                Set(1f);
                if (loop)
                {
                    yield return new WaitForSeconds(holdTime);
                    Set(0f);
                    yield return new WaitForSeconds(offTime);
                }
            } while (loop);
        }

        void Set(float v)
        {
            ignition = Mathf.Clamp01(v);
            Apply();
        }

        void LateUpdate() => Apply();                       // Animation Track changes in Play mode, light flicker
        void OnDidApplyAnimationProperties() => Apply();    // Timeline scrubbing / preview in Edit mode
        void OnValidate() { if (isActiveAndEnabled) Apply(); }
        void OnEnable() => Apply();

        void OnDisable()
        {
            foreach (var r in Targets()) if (r != null) r.SetPropertyBlock(null);
        }

        Renderer[] Targets()
        {
            if (engineRenderers != null && engineRenderers.Length > 0) return engineRenderers;
            return GetComponentsInChildren<Renderer>(true);
        }

        void Apply()
        {
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            Material mat = null;
            foreach (var r in Targets())
            {
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetFloat(IgnitionId, ignition);
                r.SetPropertyBlock(_mpb);
                if (mat == null) mat = r.sharedMaterial;
            }

            if (engineLight == null) return;
            // Same ramp as JetEngine.hlsl, so the light matches the glow.
            float I = Mathf.Clamp01(ignition);
            float heat = SmoothStep(0.02f, 0.30f, I);
            float temp = SmoothStep(0.25f, 0.90f, I);
            float bright = heat * (0.4f + 0.6f * temp * temp);

            Color cold = new Color(1f, 0.15f, 0.75f), mid = new Color(0.45f, 0.35f, 1f), hot = new Color(0.25f, 0.75f, 1f);
            if (mat != null)
            {
                if (mat.HasProperty(ColdId)) cold = mat.GetColor(ColdId);
                if (mat.HasProperty(MidId))  mid  = mat.GetColor(MidId);
                if (mat.HasProperty(HotId))  hot  = mat.GetColor(HotId);
            }
            Color c = Color.Lerp(Color.Lerp(cold, mid, Mathf.Clamp01(temp * 2f)), hot, Mathf.Clamp01(temp * 2f - 1f));
            float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (m > 1e-4f) c /= m;
            c.a = 1f;

            float fl = 1f;
            if (lightFlicker > 0f)
            {
                float igniting = Mathf.Clamp01(heat * (1f - temp) * 1.5f);
                float tm = Application.isPlaying ? Time.time : (float)Time.realtimeSinceStartupAsDouble;
                fl = 1f + lightFlicker * (Mathf.PerlinNoise(tm * 23f, 0.37f) - 0.5f) * (0.4f + 1.6f * igniting);
            }

            engineLight.color = c;
            engineLight.intensity = lightMaxIntensity * bright * bright * fl;   // squared: the walls light up later than the disc
            engineLight.enabled = bright > 0.001f;
        }

        static float SmoothStep(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }
    }
}
