using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;

namespace BF6Intro.EditorTools
{
    public static class BF6IntroSetup
    {
        const string Root = "Assets/BF6Intro";
        const string ShaderPath = Root + "/Shaders/SG_VideoLens.shadergraph";
        const string MaterialPath = Root + "/Materials/M_VideoLens.mat";
        const string RTPath = Root + "/Materials/RT_IntroVideo.renderTexture";
        const string DefaultClipPath = "Assets/Reference/bf6_intro.mp4";

        // ------------------------------------------------------------------
        // Apply the video lens to an existing mesh (e.g. Ball_Part of the material ball).
        // ------------------------------------------------------------------
        [MenuItem("Tools/BF6 Intro/Apply Video Lens To Selected Object")]
        public static void ApplyToSelected()
        {
            var go = Selection.activeGameObject;
            if (go == null || go.GetComponent<MeshFilter>() == null || go.GetComponent<MeshRenderer>() == null)
            {
                EditorUtility.DisplayDialog("BF6 Intro", "Select a GameObject with a MeshFilter and MeshRenderer (e.g. Ball_Part).", "OK");
                return;
            }
            ApplyTo(go);
        }

        public static void ApplyTo(GameObject go)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null)
            {
                EditorUtility.DisplayDialog("BF6 Intro", $"Shader not found at {ShaderPath}.", "OK");
                return;
            }
            var clip = FindClip();
            if (!AssetDatabase.IsValidFolder(Root + "/Materials")) AssetDatabase.CreateFolder(Root, "Materials");
            var rt = GetOrCreateRT(clip);

            // Per-object material so its lens fit doesn't fight other objects.
            string matPath = $"{Root}/Materials/M_VideoLens_{go.name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, matPath);
            }

            // Fit the virtual video plane to the mesh: center on its bounds, size to its radius.
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            var b = mesh.bounds;
            float r = Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z));
            mat.SetTexture("_VideoTex", rt);
            mat.SetFloat("_MappingMode", 1f);
            mat.SetVector("_LensCenter", b.center);
            mat.SetFloat("_ProjectionSize", r * 2f * 0.95f);
            mat.SetFloat("_InteriorDepth", r * 0.6f);

            var cam = Camera.main != null ? Camera.main.transform
                    : (SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera.transform : null);
            if (cam != null)
            {
                VideoRenderTextureBinder.ComputeLensBasis(go.transform, cam, out var f, out var u);
                mat.SetVector("_LensForward", f);
                mat.SetVector("_LensUp", u);
            }
            EditorUtility.SetDirty(mat);

            // Swap every material slot on this renderer (keeps the slot count).
            var mr = go.GetComponent<MeshRenderer>();
            Undo.RecordObject(mr, "Apply BF6 Video Lens");
            var mats = mr.sharedMaterials;
            for (int i = 0; i < mats.Length; i++) mats[i] = mat;
            mr.sharedMaterials = mats;

            // Explicit null checks: GetComponent can return Unity's "fake null" in the Editor, which ?? does not catch.
            var vp = go.GetComponent<VideoPlayer>();
            if (vp == null) vp = Undo.AddComponent<VideoPlayer>(go);
            vp.playOnAwake = false;
            vp.isLooping = true;
            vp.renderMode = VideoRenderMode.RenderTexture;
            vp.targetTexture = rt;
            vp.audioOutputMode = VideoAudioOutputMode.Direct;
            if (clip != null) vp.clip = clip;

            var binder = go.GetComponent<VideoRenderTextureBinder>();
            if (binder == null) binder = Undo.AddComponent<VideoRenderTextureBinder>(go);
            binder.renderTexture = rt;
            binder.targetRenderer = mr;
            binder.aimLensAtCamera = true;

            EditorUtility.SetDirty(go);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[BF6 Intro] Video lens applied to '{go.name}' (radius {r:0.###}, material {matPath}). Press Play.", go);
        }

        // ------------------------------------------------------------------
        // Version 3: keeps the original surface (gold) outside the window and fades
        // it into the video. Sphere-fits the mesh and aims the window at the logo side.
        // Version 1 above is intentionally left unchanged.
        // ------------------------------------------------------------------
        const string ShaderPath3 = Root + "/Shaders/SG_VideoLens3.shadergraph";

        [MenuItem("Tools/BF6 Intro/Apply Video Lens To Selected Object 3")]
        public static void ApplyToSelected3()
        {
            var go = Selection.activeGameObject;
            if (go == null || go.GetComponent<MeshFilter>() == null || go.GetComponent<MeshRenderer>() == null)
            {
                EditorUtility.DisplayDialog("BF6 Intro", "Select a GameObject with a MeshFilter and MeshRenderer (e.g. Ball_Part).", "OK");
                return;
            }
            // Any other selected meshes become "extra parts" that continue the same lens/window.
            var extras = Selection.gameObjects.Where(g => g != go && g.GetComponent<MeshRenderer>() != null).ToList();
            ApplyTo3(go, extras);
        }

        /// <param name="go">The main shell (sphere-fit, owns the VideoPlayer).</param>
        /// <param name="extras">Other parts that should continue the effect seamlessly. If null/empty,
        /// a sibling named "Front_Part" (the ring that fills the material ball's opening) is used.</param>
        public static void ApplyTo3(GameObject go, System.Collections.Generic.List<GameObject> extras = null)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath3);
            if (shader == null)
            {
                EditorUtility.DisplayDialog("BF6 Intro", $"Shader not found at {ShaderPath3}.", "OK");
                return;
            }
            var clip = FindClip();
            if (!AssetDatabase.IsValidFolder(Root + "/Materials")) AssetDatabase.CreateFolder(Root, "Materials");
            var rt = GetOrCreateRT(clip);
            var mr = go.GetComponent<MeshRenderer>();

            // Original surface: the renderer's current material, unless it's already one of ours,
            // in which case fall back to a material named "Gold" (the material ball's original).
            Material original = mr.sharedMaterial;
            if (original == null || original.shader.name.Contains("SG_VideoLens"))
            {
                original = AssetDatabase.FindAssets("Gold t:Material")
                    .Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)))
                    .FirstOrDefault(m => m != null && m.name == "Gold");
            }

            string matPath = $"{Root}/Materials/M_VideoLens3_{go.name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            bool isNew = mat == null;
            if (isNew)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, matPath);
            }

            if (original != null)
            {
                if (original.HasProperty("_BaseColor")) mat.SetColor("_OutsideColor", original.GetColor("_BaseColor"));
                if (original.HasProperty("_Metallic")) mat.SetFloat("_OutsideMetallic", original.GetFloat("_Metallic"));
                if (original.HasProperty("_Smoothness")) mat.SetFloat("_OutsideSmoothness", original.GetFloat("_Smoothness"));
            }

            // True sphere center/radius (the shell has an opening, so bounds.center is off).
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            FitSphere(mesh.vertices, out var center, out var radius);

            mat.SetTexture("_VideoTex", rt);
            mat.SetFloat("_MappingMode", 1f);
            mat.SetVector("_LensCenter", center);
            mat.SetFloat("_MeshRadius", radius);
            // Bigger virtual screen so the footage still covers the ball near the rim.
            mat.SetFloat("_ProjectionSize", radius * 2f * 1.3f);
            mat.SetFloat("_InteriorDepth", radius * 0.6f);

            // "More video" look preset (re-applied every time this menu runs).
            mat.SetFloat("_MaskRadius", 1.1f);        // window covers most of the front
            mat.SetFloat("_MaskSoftness", 0.5f);      // still a smooth gold -> video fade
            mat.SetFloat("_FadeCurve", 0.6f);         // video takes over sooner
            mat.SetFloat("_VideoSpread", 2.0f);       // footage glows through the gold fade zone
            mat.SetFloat("_MaskFrontCutoff", 0.55f);  // can wrap further around the sides
            mat.SetFloat("_VideoIntensity", 3.0f);
            mat.SetFloat("_Vignette", 0.35f);
            mat.SetFloat("_MaskDebug", 0f);

            // Window direction: toward a sibling front part / logo if there is one, else the camera.
            Vector3 maskF;
            var front = FindSibling(go.transform, "Front_Part_Logo", "Front_Part");
            var cam = GetAimCamera();
            if (front != null && front.GetComponent<Renderer>() != null)
                maskF = (go.transform.InverseTransformPoint(front.GetComponent<Renderer>().bounds.center) - center).normalized;
            else if (cam != null)
                VideoRenderTextureBinder.ComputeLensBasis(go.transform, cam, out maskF, out _);
            else
                maskF = new Vector3(0, 0, -1);
            Vector3 maskU = go.transform.InverseTransformDirection(Vector3.up).normalized;

            mat.SetVector("_LensForward", maskF);
            mat.SetVector("_LensUp", maskU);
            if (isNew)
            {
                mat.SetVector("_MaskForward", maskF);
                mat.SetVector("_MaskUp", maskU);
            }
            EditorUtility.SetDirty(mat);

            Undo.RecordObject(mr, "Apply BF6 Video Lens 3");
            AssignAll(mr, mat);

            // ---- Extra parts: same lens + window, converted into each part's own object space ----
            if (extras == null || extras.Count == 0)
            {
                var fp = FindSibling(go.transform, "Front_Part");
                extras = new System.Collections.Generic.List<GameObject>();
                if (fp != null && fp.GetComponent<MeshRenderer>() != null) extras.Add(fp.gameObject);
            }
            var extraRenderers = new System.Collections.Generic.List<Renderer>();
            foreach (var e in extras)
            {
                var emr = e.GetComponent<MeshRenderer>();
                var eOriginal = emr.sharedMaterial;
                string ePath = $"{Root}/Materials/M_VideoLens3_{e.name}.mat";
                var eMat = AssetDatabase.LoadAssetAtPath<Material>(ePath);
                if (eMat == null) { eMat = new Material(shader); AssetDatabase.CreateAsset(eMat, ePath); }

                // Keep this part's previous outside look if it already had one of our materials.
                Color oc = eMat.GetColor("_OutsideColor"); float om = eMat.GetFloat("_OutsideMetallic"), os = eMat.GetFloat("_OutsideSmoothness");
                eMat.CopyPropertiesFromMaterial(mat);
                eMat.SetColor("_OutsideColor", oc); eMat.SetFloat("_OutsideMetallic", om); eMat.SetFloat("_OutsideSmoothness", os);
                if (eOriginal != null && !eOriginal.shader.name.Contains("SG_VideoLens"))
                {
                    if (eOriginal.HasProperty("_BaseColor")) eMat.SetColor("_OutsideColor", eOriginal.GetColor("_BaseColor"));
                    if (eOriginal.HasProperty("_Metallic")) eMat.SetFloat("_OutsideMetallic", eOriginal.GetFloat("_Metallic"));
                    if (eOriginal.HasProperty("_Smoothness")) eMat.SetFloat("_OutsideSmoothness", eOriginal.GetFloat("_Smoothness"));
                }

                float ratio = go.transform.lossyScale.x / Mathf.Max(1e-6f, e.transform.lossyScale.x);
                eMat.SetVector("_LensCenter", e.transform.InverseTransformPoint(go.transform.TransformPoint(center)));
                eMat.SetFloat("_MeshRadius", radius * ratio);
                eMat.SetFloat("_ProjectionSize", mat.GetFloat("_ProjectionSize") * ratio);
                eMat.SetFloat("_InteriorDepth", mat.GetFloat("_InteriorDepth") * ratio);
                foreach (var prop in new[] { "_LensForward", "_LensUp", "_MaskForward", "_MaskUp" })
                {
                    Vector3 v = mat.GetVector(prop);
                    eMat.SetVector(prop, e.transform.InverseTransformDirection(go.transform.TransformDirection(v)).normalized);
                }
                EditorUtility.SetDirty(eMat);

                Undo.RecordObject(emr, "Apply BF6 Video Lens 3");
                AssignAll(emr, eMat);
                extraRenderers.Add(emr);
            }

            var vp = go.GetComponent<VideoPlayer>();
            if (vp == null) vp = Undo.AddComponent<VideoPlayer>(go);
            Undo.RecordObject(vp, "Apply BF6 Video Lens 3");
            vp.playOnAwake = false;
            vp.isLooping = true;
            vp.renderMode = VideoRenderMode.RenderTexture;
            vp.targetTexture = rt;
            vp.audioOutputMode = VideoAudioOutputMode.None;
            if (clip != null) vp.clip = clip;

            var binder = go.GetComponent<VideoRenderTextureBinder>();
            if (binder == null) binder = Undo.AddComponent<VideoRenderTextureBinder>(go);
            binder.renderTexture = rt;
            binder.targetRenderer = mr;
            binder.aimLensAtCamera = true;
            binder.muteAudio = true;
            binder.extraRenderers = extraRenderers.ToArray();

            EditorUtility.SetDirty(go);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            AssetDatabase.SaveAssets();
            Selection.activeObject = mat;
            Debug.Log($"[BF6 Intro] Video lens 3 applied to '{go.name}' + [{string.Join(", ", extras.Select(x => x.name))}]. Sphere center {center}, radius {radius:0.###}, " +
                      $"window aimed {(front != null ? "at " + front.name : "at camera")}, surface from '{(original != null ? original.name : "defaults")}'.", go);
        }

        static void AssignAll(MeshRenderer r, Material m)
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++) mats[i] = m;
            r.sharedMaterials = mats;
        }

        static Transform FindSibling(Transform t, params string[] names)
        {
            var root = t.parent != null ? t.parent : t;
            foreach (var n in names)
            {
                var found = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(c => c.name == n);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Least-squares sphere fit: x²+y²+z² = 2ax + 2by + 2cz + e.</summary>
        static void FitSphere(Vector3[] pts, out Vector3 center, out float radius)
        {
            var A = new double[4, 5];
            foreach (var p in pts)
            {
                double[] row = { p.x, p.y, p.z, 1.0 };
                double rhs = p.x * p.x + p.y * p.y + p.z * p.z;
                for (int i = 0; i < 4; i++)
                {
                    for (int j = 0; j < 4; j++) A[i, j] += row[i] * row[j];
                    A[i, 4] += row[i] * rhs;
                }
            }
            // Gaussian elimination with partial pivoting.
            for (int c = 0; c < 4; c++)
            {
                int piv = c;
                for (int r = c + 1; r < 4; r++) if (System.Math.Abs(A[r, c]) > System.Math.Abs(A[piv, c])) piv = r;
                for (int k = 0; k < 5; k++) { var tmp = A[c, k]; A[c, k] = A[piv, k]; A[piv, k] = tmp; }
                if (System.Math.Abs(A[c, c]) < 1e-12) { center = Vector3.zero; radius = 0.5f; return; }
                for (int r = 0; r < 4; r++)
                {
                    if (r == c) continue;
                    double f = A[r, c] / A[c, c];
                    for (int k = c; k < 5; k++) A[r, k] -= f * A[c, k];
                }
            }
            double a = A[0, 4] / A[0, 0], b = A[1, 4] / A[1, 1], cc = A[2, 4] / A[2, 2], e = A[3, 4] / A[3, 3];
            // Solved for (2a, 2b, 2c, e).
            center = new Vector3((float)(a * 0.5), (float)(b * 0.5), (float)(cc * 0.5));
            radius = (float)System.Math.Sqrt(System.Math.Max(1e-8, e + center.sqrMagnitude));
        }

        static Transform GetAimCamera()
        {
            if (Camera.main != null) return Camera.main.transform;
            return SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera.transform : null;
        }

        static VideoClip FindClip()
        {
            return Selection.objects.OfType<VideoClip>().FirstOrDefault()
                   ?? AssetDatabase.LoadAssetAtPath<VideoClip>(DefaultClipPath)
                   ?? AssetDatabase.FindAssets("t:VideoClip").Select(g => AssetDatabase.LoadAssetAtPath<VideoClip>(AssetDatabase.GUIDToAssetPath(g))).FirstOrDefault();
        }

        static RenderTexture GetOrCreateRT(VideoClip clip)
        {
            var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(RTPath);
            if (rt != null) return rt;
            int w = clip != null ? (int)clip.width : 1920;
            int h = clip != null ? (int)clip.height : 1080;
            rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                useMipMap = false, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
            };
            AssetDatabase.CreateAsset(rt, RTPath);
            return rt;
        }

        [MenuItem("Tools/BF6 Intro/Create Video Lens Demo")]
        public static void CreateDemo()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null)
            {
                EditorUtility.DisplayDialog("BF6 Intro", $"Shader not found at {ShaderPath}. Let Unity finish importing and try again.", "OK");
                return;
            }

            // Video clip: selected clip > reference clip > first clip in the project.
            var clip = Selection.activeObject as VideoClip
                       ?? AssetDatabase.LoadAssetAtPath<VideoClip>(DefaultClipPath)
                       ?? AssetDatabase.FindAssets("t:VideoClip").Select(g => AssetDatabase.LoadAssetAtPath<VideoClip>(AssetDatabase.GUIDToAssetPath(g))).FirstOrDefault();

            if (!AssetDatabase.IsValidFolder(Root + "/Materials")) AssetDatabase.CreateFolder(Root, "Materials");

            // RenderTexture asset (sRGB, no mips, clamped).
            var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(RTPath);
            if (rt == null)
            {
                int w = clip != null ? (int)clip.width : 1920;
                int h = clip != null ? (int)clip.height : 1080;
                rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    useMipMap = false, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
                };
                AssetDatabase.CreateAsset(rt, RTPath);
            }

            // Material.
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, MaterialPath);
            }
            mat.SetTexture("_VideoTex", rt);
            EditorUtility.SetDirty(mat);

            // Lens object: an egg-shaped sphere whose -Z side (the lens face) looks at the camera.
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "BF6 Video Lens";
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.localScale = new Vector3(1f, 1f, 1.6f);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;

            var cam = Camera.main != null ? Camera.main.transform : null;
            var view = SceneView.lastActiveSceneView;
            Transform anchor = cam != null ? cam : (view != null ? view.camera.transform : null);
            if (anchor != null)
            {
                go.transform.position = anchor.position + anchor.forward * 3f;
                // Object +Z points away from the camera, so its -Z lens face points at it,
                // tilted a little like the BF6 shot.
                go.transform.rotation = Quaternion.LookRotation(anchor.forward, anchor.up) * Quaternion.Euler(-12f, 25f, 0f);
            }

            var vp = go.AddComponent<VideoPlayer>();
            vp.playOnAwake = false;
            vp.isLooping = true;
            vp.renderMode = VideoRenderMode.RenderTexture;
            vp.targetTexture = rt;
            vp.audioOutputMode = VideoAudioOutputMode.Direct;
            if (clip != null) vp.clip = clip;

            var binder = go.AddComponent<VideoRenderTextureBinder>();
            binder.renderTexture = rt;
            binder.targetRenderer = go.GetComponent<MeshRenderer>();

            Undo.RegisterCreatedObjectUndo(go, "Create BF6 Video Lens");
            Selection.activeGameObject = go;
            AssetDatabase.SaveAssets();

            if (clip == null)
                Debug.LogWarning("[BF6 Intro] No VideoClip found. Assign one on the VideoPlayer of 'BF6 Video Lens'.");
            else
                Debug.Log($"[BF6 Intro] Video lens created with clip '{clip.name}'. Press Play.");
        }
    }
}
