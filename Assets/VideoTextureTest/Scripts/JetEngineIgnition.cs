using System.Collections;
using UnityEngine;

namespace VideoTextureTest
{
    /// <summary>
    /// Drives the SG_JetEngine ignition (engine off -> magenta first light -> violet -> full blue/cyan):
    /// - "_Ignition" on the engine renderers (and on the SG_JetHaze heat-haze renderers),
    /// - the fan spin: straight solid blades turning slowly anti-clockwise at the start; late in the
    ///   ignition the fan slows, reverses and speeds up clockwise while the blades twist into spirals.
    ///   The angle is accumulated here (with inertia), so the direction change is smooth,
    /// - the animation clock (_EffectTime) so the shimmer / haze also move in Edit mode,
    /// - an optional light inside the nozzle that follows the shader's colour and brightness.
    ///
    /// Auto: with Play On Start, the ignition plays by itself when entering Play mode.
    /// Timeline: turn Play On Start off and keyframe <see cref="ignition"/> with a regular
    /// Animation Track (it previews while scrubbing in Edit mode).
    /// Uses a MaterialPropertyBlock, so the material assets are never modified.
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

        [Header("Spin")]
        [Tooltip("Turns per second at the start of the ignition (anti-clockwise, solid blades).")]
        [Min(0f)] public float startSpinSpeed = 0.3f;
        [Tooltip("Turns per second at full power (clockwise, spiral blades). On screen it is capped by Max Blades Per Frame so it never strobes.")]
        [Min(0f)] public float endSpinSpeed = 1.5f;
        [Tooltip("Ignition range over which the fan slows, reverses and speeds up the other way.")]
        public Vector2 reverseRange = new Vector2(0.55f, 0.95f);
        [Tooltip("How quickly the fan follows the target speed (higher = less inertia).")]
        [Min(0.01f)] public float spinResponse = 3f;
        [Tooltip("Motion blur as a fraction of each displayed frame (0.5 = film-like 180° shutter). Uses the real frame time, so it's right at any frame rate.")]
        [Range(0f, 1f)] public float shutterAngle = 0.5f;
        [Tooltip("Most the fan may advance between displayed frames, in blades. Near 0.5 a fan looks frozen or runs backwards (wagon-wheel); keeping it below ~0.35 always reads as forward motion. The speed is capped to this on screen.")]
        [Range(0.05f, 0.45f)] public float maxBladesPerFrame = 0.3f;
        [Tooltip("Spin and shimmer while not in Play mode (keeps the Editor repainting while the engine is on).")]
        public bool animateInEditMode = true;

        [Header("Targets")]
        [Tooltip("Renderers using SG_JetEngine. Empty = child renderers using SG_JetEngine.")]
        public Renderer[] engineRenderers;
        [Tooltip("Renderers using SG_JetHaze (exhaust heat haze). Empty = child renderers using SG_JetHaze.")]
        public Renderer[] hazeRenderers;
        [Tooltip("Optional light inside the nozzle; takes the shader's colour and brightness.")]
        public Light engineLight;
        [Min(0f)] public float lightMaxIntensity = 4f;
        [Range(0f, 1f)] public float lightFlicker = 0.15f;

        static readonly int IgnitionId = Shader.PropertyToID("_Ignition");
        static readonly int SpinPhaseId = Shader.PropertyToID("_SpinPhase");
        static readonly int SpinBlurId = Shader.PropertyToID("_SpinBlur");
        static readonly int EffectTimeId = Shader.PropertyToID("_EffectTime");
        static readonly int BladeCountId = Shader.PropertyToID("_BladeCount");
        static readonly int ColdId = Shader.PropertyToID("_ColdColor");
        static readonly int MidId = Shader.PropertyToID("_MidColor");
        static readonly int HotId = Shader.PropertyToID("_HotColor");

        MaterialPropertyBlock _mpb;
        float _spinPhase;     // turns (+ = anti-clockwise on screen when looking into the nozzle)
        float _spinVelocity;  // turns / s, signed, with inertia
        double _lastTime = -1;
        float _frameDt = 1f / 60f;   // smoothed time between Game-view renders
        double _lastRender = -1;

        /// <summary>Signed target spin speed for an ignition value (+ anti-clockwise, - clockwise).</summary>
        public float TargetSpinVelocity(float ig)
        {
            float on = SmoothStep(0.02f, 0.15f, ig);
            float s = SmoothStep(reverseRange.x, Mathf.Max(reverseRange.x + 0.01f, reverseRange.y), ig);
            return Mathf.Lerp(startSpinSpeed, -endSpinSpeed, s) * on;
        }

        void Start()
        {
            if (!Application.isPlaying) return;
            _spinVelocity = 0f;
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

        void Update() => Tick();
        void OnDidApplyAnimationProperties() => Apply();    // Timeline scrubbing / preview in Edit mode
        void OnValidate() { if (isActiveAndEnabled) Apply(); }

        void OnEnable()
        {
            _lastTime = -1;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += EditorTick;
#endif
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            Apply();
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
#endif
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            foreach (var r in Collect(engineRenderers, "SG_JetEngine")) if (r != null) r.SetPropertyBlock(null);
            foreach (var r in Collect(hazeRenderers, "SG_JetHaze")) if (r != null) r.SetPropertyBlock(null);
        }

#if UNITY_EDITOR
        void EditorTick()
        {
            if (Application.isPlaying || !animateInEditMode || this == null) return;
            if (ignition <= 0.001f && Mathf.Abs(_spinVelocity) <= 0.001f) return;
            Tick();
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();    // keeps the Game view updating
            UnityEditor.SceneView.RepaintAll();
        }
#endif

        // Measures how often the Game view actually renders (Edit and Play mode), for blur and the anti-strobe cap.
        void OnBeginCamera(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == null || cam.cameraType != CameraType.Game) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (_lastRender > 0)
            {
                float d = (float)(now - _lastRender);
                if (d > 0.0005f && d < 0.5f) _frameDt = Mathf.Lerp(_frameDt, d, 0.1f);
            }
            _lastRender = now;
        }

        /// <summary>Smoothed time between Game-view frames (s).</summary>
        public float FrameTime => _frameDt;

        double Now() => Application.isPlaying ? Time.timeAsDouble : Time.realtimeSinceStartupAsDouble;

        void Tick()
        {
            double now = Now();
            float dt = _lastTime < 0 ? 0f : Mathf.Clamp((float)(now - _lastTime), 0f, 0.1f);
            _lastTime = now;
            if (Application.isPlaying) dt = Time.deltaTime;

            float target = TargetSpinVelocity(Mathf.Clamp01(ignition));
            _spinVelocity = Mathf.Lerp(_spinVelocity, target, 1f - Mathf.Exp(-dt * spinResponse));
            _spinPhase = Mathf.Repeat(_spinPhase + DisplayVelocity() * dt, 1000f);
            Apply();
        }

        float Blades()
        {
            foreach (var r in Collect(engineRenderers, "SG_JetEngine"))
                if (r != null && r.sharedMaterial != null && r.sharedMaterial.HasProperty(BladeCountId)) return Mathf.Max(1f, r.sharedMaterial.GetFloat(BladeCountId));
            return 30f;
        }

        /// <summary>Spin speed as shown on screen: capped so the fan never strobes (looks frozen / backwards).</summary>
        public float DisplayVelocity()
        {
            float cap = maxBladesPerFrame / (Blades() * Mathf.Max(_frameDt, 1e-3f));   // turns per second
            return Mathf.Clamp(_spinVelocity, -cap, cap);
        }

        Renderer[] Collect(Renderer[] list, string shaderName)
        {
            if (list != null && list.Length > 0) return list;
            var res = new System.Collections.Generic.List<Renderer>();
            foreach (var r in GetComponentsInChildren<Renderer>(true))
                if (r.sharedMaterial != null && r.sharedMaterial.shader != null && r.sharedMaterial.shader.name.EndsWith(shaderName)) res.Add(r);
            return res.ToArray();
        }

        void Apply()
        {
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            float effectTime = (float)(Now() % 3600.0) + 0.001f;

            Material mat = null;
            foreach (var r in Collect(engineRenderers, "SG_JetEngine"))
            {
                if (r == null) continue;
                if (mat == null) mat = r.sharedMaterial;
                r.GetPropertyBlock(_mpb);
                _mpb.SetFloat(IgnitionId, ignition);
                _mpb.SetFloat(SpinPhaseId, _spinPhase);
                _mpb.SetFloat(SpinBlurId, DisplayVelocity() * Blades() * _frameDt * shutterAngle);   // signed blades swept while the shutter is open
                _mpb.SetFloat(EffectTimeId, effectTime);
                r.SetPropertyBlock(_mpb);
            }
            foreach (var r in Collect(hazeRenderers, "SG_JetHaze"))
            {
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetFloat(IgnitionId, ignition);
                _mpb.SetFloat(EffectTimeId, effectTime);
                r.SetPropertyBlock(_mpb);
            }

            if (engineLight == null) return;
            // Same ramp as JetEngine.hlsl, so the light matches the glow.
            float I = Mathf.Clamp01(ignition);
            float heat = SmoothStep(0.02f, 0.30f, I);
            float temp = SmoothStep(0.25f, 0.90f, I);
            float bright = heat * (0.4f + 0.6f * temp * temp);

            Color cold = new Color(1f, 0.15f, 0.75f), mid = new Color(0.42f, 0.22f, 1f), hot = new Color(0.08f, 0.5f, 1f);
            if (mat != null)
            {
                if (mat.HasProperty(ColdId)) cold = mat.GetColor(ColdId);
                if (mat.HasProperty(MidId)) mid = mat.GetColor(MidId);
                if (mat.HasProperty(HotId)) hot = mat.GetColor(HotId);
            }
            Color c = Color.Lerp(Color.Lerp(cold, mid, Mathf.Clamp01(temp * 2f)), hot, Mathf.Clamp01(temp * 2f - 1f));
            float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (m > 1e-4f) c /= m;
            c.a = 1f;

            float fl = 1f;
            if (lightFlicker > 0f)
            {
                float igniting = Mathf.Clamp01(heat * (1f - temp) * 1.5f);
                fl = 1f + lightFlicker * (Mathf.PerlinNoise((float)Now() * 23f, 0.37f) - 0.5f) * (0.4f + 1.6f * igniting);
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
