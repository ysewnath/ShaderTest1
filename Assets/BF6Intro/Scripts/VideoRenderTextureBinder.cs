using System.Collections;
using UnityEngine;
using UnityEngine.Video;

namespace BF6Intro
{
    /// <summary>
    /// Plays a VideoClip into a RenderTexture and feeds it to the SG_VideoLens material
    /// (or any material with a texture property). Also drives the shader's "_Reveal"
    /// power-on animation when playback starts.
    /// </summary>
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

        [Header("Power-on reveal")]
        public bool animateReveal = true;
        [Min(0.01f)] public float revealDuration = 1.25f;
        public AnimationCurve revealCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        VideoPlayer _player;
        MaterialPropertyBlock _mpb;
        bool _ownsTexture;
        int _texId, _revealId;

        public RenderTexture Texture => renderTexture;

        void OnEnable()
        {
            _player = GetComponent<VideoPlayer>();
            if (targetRenderer == null) targetRenderer = GetComponent<Renderer>();
            _mpb ??= new MaterialPropertyBlock();
            _texId = Shader.PropertyToID(textureProperty);
            _revealId = Shader.PropertyToID(revealProperty);

            EnsureRenderTexture();

            _player.playOnAwake = false;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.targetTexture = renderTexture;
            _player.prepareCompleted += OnPrepared;

            SetReveal(animateReveal ? 0f : 1f);
            _player.Prepare();
        }

        void OnDisable()
        {
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
            if (targetRenderer == null) return;
            targetRenderer.GetPropertyBlock(_mpb);
            if (renderTexture != null) _mpb.SetTexture(_texId, renderTexture);
            _mpb.SetFloat(_revealId, value);
            targetRenderer.SetPropertyBlock(_mpb);
        }
    }
}
