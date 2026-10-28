using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace VideoTextureTest
{
    /// <summary>
    /// Drives the SG_GlowDecal "_Reveal" (ignite: the logo burns in left-to-right with a
    /// white-hot leading edge).
    ///
    /// Auto: with Play On Start, the ignite plays by itself when entering Play mode.
    /// Timeline: turn Play On Start off and keyframe <see cref="reveal"/> with a regular
    /// Animation Track (it previews while scrubbing in Edit mode).
    /// In Play mode it works on a runtime copy of the material so the asset isn't modified.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(DecalProjector))]
    public class GlowDecalAnimator : MonoBehaviour
    {
        [Tooltip("Ignite automatically when entering Play mode. Turn OFF when a Timeline animates Reveal.")]
        public bool playOnStart = true;

        [Tooltip("Shader Reveal / ignite amount (0 = off, 1 = fully lit). Animatable: keyframe it with an Animation Track in Timeline.")]
        [Range(0f, 1f)] public float reveal = 1f;

        [Header("Play On Start ignite")]
        [Min(0f)] public float delay = 0.25f;
        [Min(0.01f)] public float igniteDuration = 1.6f;
        public AnimationCurve igniteCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
        public bool loop = false;
        [Min(0f)] public float holdTime = 3f;

        [Tooltip("Optional light that brightens with the logo (fakes orange spill onto nearby hardware).")]
        public Light spillLight;
        [Min(0f)] public float spillLightMaxIntensity = 0f;

        static readonly int RevealId = Shader.PropertyToID("_Reveal");
        DecalProjector _projector;
        Material _instance;

        void Start()
        {
            if (!Application.isPlaying) return;
            _projector = GetComponent<DecalProjector>();
            if (_projector.material == null) return;
            _instance = new Material(_projector.material) { name = _projector.material.name + " (Instance)" };
            _projector.material = _instance;
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
                if (loop) yield return new WaitForSeconds(holdTime);
            } while (loop);
        }

        void Set(float v)
        {
            reveal = Mathf.Clamp01(v);
            Apply();
        }

        void LateUpdate() => Apply();                       // picks up Animation Track changes in Play mode
        void OnDidApplyAnimationProperties() => Apply();    // Timeline scrubbing / preview in Edit mode
        void OnValidate() { if (isActiveAndEnabled) Apply(); }

        void Apply()
        {
            if (_projector == null) _projector = GetComponent<DecalProjector>();
            // Play mode: the runtime copy. Edit mode (preview): the projector's material, only when it changes.
            Material m = Application.isPlaying ? _instance : (_projector != null ? _projector.material : null);
            if (m != null && m.HasProperty(RevealId) && !Mathf.Approximately(m.GetFloat(RevealId), reveal))
                m.SetFloat(RevealId, reveal);
            if (spillLight != null && spillLightMaxIntensity > 0f) spillLight.intensity = spillLightMaxIntensity * reveal;
        }

        void OnDestroy()
        {
            if (_instance != null) Destroy(_instance);
        }
    }
}
