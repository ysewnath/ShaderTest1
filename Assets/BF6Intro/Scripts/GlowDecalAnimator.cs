using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace BF6Intro
{
    /// <summary>
    /// Plays the "ignite" reveal on a SG_GlowDecal projector: the logo burns in left-to-right
    /// with a white-hot leading edge, then settles into its flickering glow.
    /// Works on a runtime copy of the material so the asset isn't modified.
    /// </summary>
    [RequireComponent(typeof(DecalProjector))]
    public class GlowDecalAnimator : MonoBehaviour
    {
        public bool playOnStart = true;
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
            _projector = GetComponent<DecalProjector>();
            if (_projector.material == null) return;
            _instance = new Material(_projector.material) { name = _projector.material.name + " (Instance)" };
            _projector.material = _instance;
            if (playOnStart) Play();
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
            if (_instance != null) _instance.SetFloat(RevealId, v);
            if (spillLight != null && spillLightMaxIntensity > 0f) spillLight.intensity = spillLightMaxIntensity * v;
        }

        void OnDestroy()
        {
            if (_instance != null) Destroy(_instance);
        }
    }
}
