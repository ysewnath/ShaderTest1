using System.Collections;
using UnityEngine;
using UnityEngine.Video;

namespace VideoTextureTest
{
    /// <summary>
    /// Plays a VideoClip into a RenderTexture and feeds it to the SG_VideoLens material
    /// (or any material with a texture property). Also drives the shader's "_Reveal"
    /// power-on animation and can keep the lens face aimed at a camera.
    ///
    /// Timeline: turn off Auto Play and keyframe <see cref="reveal"/> with a regular
    /// Animation Track (it previews while scrubbing in Edit mode).
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(VideoPlayer))]
    [DisallowMultipleComponent]
    public class VideoRenderTextureBinder : MonoBehaviour
    {
        [Tooltip("Renderer that uses the SG_VideoLens material. Defaults to the Renderer on this GameObject.")]
        public Renderer targetRenderer;

        [Tooltip("Optional RenderTexture asset. If empty, one is created at runtime.")]
        public RenderTexture renderTexture;

        [Tooltip("Size of the runtime RenderTexture when none is assigned and the clip size is unknown.")]
        public Vector2Int fallbackResolution = new Vector2Int(1920, 1080);

        [Tooltip("Create the runtime RenderTexture at the clip's native resolution.")]
        public bool matchClipResolution = true;

        public string textureProperty = "_VideoTex";
        public string revealProperty = "_Reveal";

        [Tooltip("Play the video without sound.")]
        public bool muteAudio = true;

        [Tooltip("Other renderers (e.g. Front_Part) that continue the same video lens. They share this VideoPlayer.")]
        public Renderer[] extraRenderers = new Renderer[0];

        [Header("Playback")]
        [Tooltip("Play the video (and the power-on below) automatically when entering Play mode. " +
                 "Turn OFF when a Timeline drives the video and Reveal.")]
        public bool autoPlay = true;

        [Tooltip("Shader Reveal / power-on amount (0 = off, 1 = fully on). Animatable: keyframe it with an Animation Track in Timeline.")]
        [Range(0f, 1f)] public float reveal = 1f;

        [Header("Auto Play power-on")]
        public bool animateReveal = true;
        [Min(0.01f)] public float revealDuration = 1.25f;
        public AnimationCurve revealCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("Lens aiming (Lens Interior mode)")]
        [Tooltip("Every frame, point the lens face (and the footage's 'up') at this camera.")]
        public bool aimLensAtCamera = true;
        [Tooltip("Camera to aim at. Defaults to Camera.main.")]
        public Camera lensCamera;

        VideoPlayer _player;
        MaterialPropertyBlock _mpb;
        bool _ownsTexture;
        int _texId, _revealId;
        static readonly int LensForwardId = Shader.PropertyToID("_LensForward");
        static readonly int LensUpId = Shader.PropertyToID("_LensUp");

        public RenderTexture Texture => renderTexture;

        void EnsureInit()
        {
            if (_player == null) _player = GetComponent<VideoPlayer>();
            if (targetRenderer == null) targetRenderer = GetComponent<Renderer>();
            _mpb ??= new MaterialPropertyBlock();
            _texId = Shader.PropertyToID(textureProperty);
            _revealId = Shader.PropertyToID(revealProperty);
        }

        void OnEnable()
        {
            EnsureInit();
            if (!Application.isPlaying)
            {
                // Edit mode: just show the current Reveal / texture (Timeline preview, scrubbing).
                Apply();
                return;
            }

            EnsureRenderTexture();

            _player.playOnAwake = false;
            if (muteAudio) _player.audioOutputMode = VideoAudioOutputMode.None;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.targetTexture = renderTexture;

            if (!autoPlay)
            {
                // A Timeline drives the video and Reveal.
                Apply();
                return;
            }

            _player.prepareCompleted += OnPrepared;
            if (animateReveal) reveal = 0f;
            Apply();
            _player.Prepare();
        }

        void OnValidate()
        {
            if (!isActiveAndEnabled) return;
            EnsureInit();
            Apply();
        }

        // Called after an Animation Track / Animator writes 'reveal' (also while scrubbing in Edit mode).
        void OnDidApplyAnimationProperties()
        {
            EnsureInit();
            Apply();
        }

        void OnDisable()
        {
            if (!Application.isPlaying) return;
            if (_player != null)
            {
                _player.prepareCompleted -= OnPrepared;
                _player.Stop();
            }
            if (_ownsTexture && renderTexture != null)
            {
                renderTexture.Release();
                Destroy(renderTexture);
                renderTexture = null;
                _ownsTexture = false;
            }
        }

        void LateUpdate()
        {
            EnsureInit();
            Apply();   // keeps Reveal (animated or not), the texture and lens aiming in sync
        }

        void EnsureRenderTexture()
        {
            if (renderTexture != null) return;

            int w = fallbackResolution.x, h = fallbackResolution.y;
            if (matchClipResolution && _player.clip != null && _player.clip.width > 0)
            {
                w = (int)_player.clip.width;
                h = (int)_player.clip.height;
            }

            // sRGB so the video's gamma-encoded pixels are linearized correctly in HDRP (linear color space).
            renderTexture = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = $"{name}_VideoRT",
                useMipMap = false,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            renderTexture.Create();
            _ownsTexture = true;
        }

        void OnPrepared(VideoPlayer vp)
        {
            vp.Play();
            if (animateReveal) StartCoroutine(RevealRoutine());
            else SetReveal(1f);
        }

        IEnumerator RevealRoutine()
        {
            float t = 0f;
            while (t < revealDuration)
            {
                t += Time.deltaTime;
                SetReveal(revealCurve.Evaluate(Mathf.Clamp01(t / revealDuration)));
                yield return null;
            }
            SetReveal(1f);
        }

        /// <summary>Set the shader's power-on amount (0 = off, 1 = fully on).</summary>
        public void SetReveal(float value)
        {
            reveal = Mathf.Clamp01(value);
            Apply();
        }

        /// <summary>Object-space lens forward/up that make the lens face the given camera.</summary>
        public static void ComputeLensBasis(Transform target, Transform cam, out Vector3 forwardOS, out Vector3 upOS)
        {
            forwardOS = target.InverseTransformDirection((cam.position - target.position).normalized).normalized;
            upOS = target.InverseTransformDirection(cam.up).normalized;
        }

        static readonly int LensCenterId = Shader.PropertyToID("_LensCenter");

        void Apply()
        {
            ApplyTo(targetRenderer);
            if (extraRenderers != null)
                foreach (var r in extraRenderers) ApplyTo(r);
        }

        void ApplyTo(Renderer r)
        {
            if (r == null) return;
            r.GetPropertyBlock(_mpb);
            if (renderTexture != null) _mpb.SetTexture(_texId, renderTexture);
            _mpb.SetFloat(_revealId, reveal);

            if (aimLensAtCamera)
            {
                var cam = lensCamera != null ? lensCamera : Camera.main;
                if (cam != null)
                {
                    // Aim from the lens center (not the pivot) so every part agrees on the direction.
                    var t = r.transform;
                    Vector3 centerOS = r.sharedMaterial != null && r.sharedMaterial.HasProperty(LensCenterId)
                        ? (Vector3)r.sharedMaterial.GetVector(LensCenterId) : Vector3.zero;
                    Vector3 centerWS = t.TransformPoint(centerOS);
                    _mpb.SetVector(LensForwardId, t.InverseTransformDirection((cam.transform.position - centerWS).normalized).normalized);
                    _mpb.SetVector(LensUpId, t.InverseTransformDirection(cam.transform.up).normalized);
                }
            }
            r.SetPropertyBlock(_mpb);
        }
    }
}
