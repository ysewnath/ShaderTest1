using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace VideoTextureTest
{
    /// <summary>
    /// HDRP custom pass that draws an outline around every enabled <see cref="OutlineHighlight"/>.
    /// 1) The highlighted renderers are drawn into an off-screen mask (coverage + their own depth).
    /// 2) A full-screen pass outlines only the parts that are actually visible in the scene depth,
    ///    so anything below the water surface (or behind other geometry) gets no outline.
    /// Nothing is drawn on the object's surface, so there's no z-fighting / flicker with the object
    /// or with other effects that share its transform.
    /// Use injection point "Before Post Process" (after the water and transparents).
    /// </summary>
    [System.Serializable]
    public class OutlineHighlightPass : CustomPass
    {
        [ColorUsage(true, true)] public Color outlineColor = Color.white;
        [Tooltip("Outline width in pixels at 1080p (scales with resolution).")]
        [Range(0.5f, 12f)] public float thickness = 3f;
        [Range(0f, 1f)] public float opacity = 1f;
        [Tooltip("Brightness. The outline is drawn before tonemapping (so it stays stable under TAA), " +
                 "so values above 1 are needed for a clean, bright white.")]
        [Min(0f)] public float intensity = 4f;
        [Tooltip("How far (relative) the object may be behind the scene depth and still count as visible. " +
                 "Raise if the outline breaks up on the object's edges, lower if it shows through thin occluders.")]
        [Range(0f, 0.05f)] public float depthTolerance = 0.01f;

        [SerializeField] Shader maskShader;
        [SerializeField] Shader compositeShader;

        Material _maskMat, _compositeMat;
        RTHandle _maskRT, _maskDepth;

        static readonly int k_OutlineMask = Shader.PropertyToID("_OutlineMask");
        static readonly int k_OutlineSceneDepth = Shader.PropertyToID("_OutlineSceneDepth");
        static readonly int k_OutlineColor = Shader.PropertyToID("_OutlineColor");
        static readonly int k_OutlineThickness = Shader.PropertyToID("_OutlineThickness");
        static readonly int k_OutlineOpacity = Shader.PropertyToID("_OutlineOpacity");
        static readonly int k_OutlineIntensity = Shader.PropertyToID("_OutlineIntensity");
        static readonly int k_DepthTolerance = Shader.PropertyToID("_DepthTolerance");

        public void SetShaders(Shader mask, Shader composite) { maskShader = mask; compositeShader = composite; }

        protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            if (maskShader == null) maskShader = Shader.Find("Hidden/VideoTextureTest/OutlineMask");
            if (compositeShader == null) compositeShader = Shader.Find("Hidden/VideoTextureTest/OutlineComposite");
            if (maskShader != null) _maskMat = CoreUtils.CreateEngineMaterial(maskShader);
            if (compositeShader != null) _compositeMat = CoreUtils.CreateEngineMaterial(compositeShader);

            _maskRT = RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
                colorFormat: GraphicsFormat.R32G32_SFloat, useDynamicScale: true, name: "Outline Highlight Mask");
            _maskDepth = RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
                depthBufferBits: DepthBits.Depth32, useDynamicScale: true, name: "Outline Highlight Mask Depth");
        }

        protected override void Execute(CustomPassContext ctx)
        {
            if (_maskMat == null || _compositeMat == null || OutlineHighlight.Active.Count == 0) return;

            // 1) Mask: coverage + eye depth of the highlighted renderers (own depth buffer, nearest surface wins).
            CoreUtils.SetRenderTarget(ctx.cmd, _maskRT, _maskDepth, ClearFlag.All, Color.clear);
            int drawn = 0;
            foreach (var h in OutlineHighlight.Active)
            {
                if (h == null || !h.isActiveAndEnabled) continue;
                foreach (var r in h.Renderers)
                {
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    int subMeshes = SubMeshCount(r);
                    for (int s = 0; s < subMeshes; s++) ctx.cmd.DrawRenderer(r, _maskMat, s, 0);
                    drawn++;
                }
            }
            if (drawn == 0) return;

            // 2) Outline over the camera color (camera depth is read, not bound).
            ctx.propertyBlock.SetTexture(k_OutlineMask, _maskRT);
            // Full camera depth incl. the water surface (the default depth texture is copied before the water).
            ctx.propertyBlock.SetTexture(k_OutlineSceneDepth, ctx.cameraDepthBuffer);
            ctx.propertyBlock.SetColor(k_OutlineColor, outlineColor);
            ctx.propertyBlock.SetFloat(k_OutlineThickness, thickness);
            ctx.propertyBlock.SetFloat(k_OutlineOpacity, opacity);
            ctx.propertyBlock.SetFloat(k_OutlineIntensity, intensity);
            ctx.propertyBlock.SetFloat(k_DepthTolerance, depthTolerance);
            CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
            CoreUtils.DrawFullScreen(ctx.cmd, _compositeMat, ctx.propertyBlock, shaderPassId: 0);
        }

        static int SubMeshCount(Renderer r)
        {
            Mesh m = null;
            if (r is SkinnedMeshRenderer smr) m = smr.sharedMesh;
            else { var mf = r.GetComponent<MeshFilter>(); if (mf != null) m = mf.sharedMesh; }
            return m != null ? Mathf.Max(1, m.subMeshCount) : 1;
        }

        protected override void Cleanup()
        {
            CoreUtils.Destroy(_maskMat);
            CoreUtils.Destroy(_compositeMat);
            _maskRT?.Release();
            _maskDepth?.Release();
        }
    }
}
