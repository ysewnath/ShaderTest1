using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;

namespace VideoTextureTest.EditorTools
{
    public static class VideoTextureTestSetup
    {
        // No hardcoded paths: the tool folder and its assets are resolved at runtime (see ToolPaths).
        static string MaterialPath => ToolPaths.AssetPath("Materials", "M_VideoLens.mat");
        static string RTPath => ToolPaths.AssetPath("Materials", "RT_VideoTexture.renderTexture");

        // ------------------------------------------------------------------
        // Apply the video lens to an existing mesh (e.g. Ball_Part of the material ball).
        // ------------------------------------------------------------------
        [MenuItem("Tools/VideoTextureTest/Apply Video Lens To Selected Object")]
        public static void ApplyToSelected()
        {
            var go = Selection.activeGameObject;
            if (go == null || go.GetComponent<MeshFilter>() == null || go.GetComponent<MeshRenderer>() == null)
            {
                EditorUtility.DisplayDialog("VideoTextureTest", "Select a GameObject with a MeshFilter and MeshRenderer (e.g. Ball_Part).", "OK");
                return;
            }
            ApplyTo(go);
        }

        public static void ApplyTo(GameObject go)
        {
            var shader = ToolPaths.FindShader("SG_VideoLens");
            if (shader == null)
            {
                EditorUtility.DisplayDialog("VideoTextureTest", "Couldn't find the SG_VideoLens shader graph in the project.", "OK");
                return;
            }
            var clip = FindClip();
            ToolPaths.EnsureFolder("Materials");
            var rt = GetOrCreateRT(clip);

            // Per-object material so its lens fit doesn't fight other objects.
            string matPath = ToolPaths.AssetPath("Materials", $"M_VideoLens_{go.name}.mat");
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
            Undo.RecordObject(mr, "Apply Video Lens");
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
            Debug.Log($"[VideoTextureTest] Video lens applied to '{go.name}' (radius {r:0.###}, material {matPath}). Press Play.", go);
        }

        // ------------------------------------------------------------------
        // Version 3 (KEPT / frozen at the user's request): keeps the original surface (gold)
        // outside the window and fades it into the video. Sphere-fits the mesh, aims the
        // window at the logo side, and continues across extra parts (Front_Part).
        // ------------------------------------------------------------------

        [MenuItem("Tools/VideoTextureTest/Apply Video Lens To Selected Object 3")]
        public static void ApplyToSelected3()
        {
            var go = Selection.activeGameObject;
            if (go == null || go.GetComponent<MeshFilter>() == null || go.GetComponent<MeshRenderer>() == null)
            {
                EditorUtility.DisplayDialog("VideoTextureTest", "Select a GameObject with a MeshFilter and MeshRenderer (e.g. Ball_Part).", "OK");
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
            var shader = ToolPaths.FindShader("SG_VideoLens3");
            if (shader == null)
            {
                EditorUtility.DisplayDialog("VideoTextureTest", "Couldn't find the SG_VideoLens3 shader graph in the project.", "OK");
                return;
            }
            var clip = FindClip();
            ToolPaths.EnsureFolder("Materials");
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

            string matPath = ToolPaths.AssetPath("Materials", $"M_VideoLens3_{go.name}.mat");
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
            mat.SetFloat("_MaskFrontCutoff", 1.0f);   // full wrap: no hard cutoff from behind (user-tuned)
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

            Undo.RecordObject(mr, "Apply Video Lens 3");
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
                string ePath = ToolPaths.AssetPath("Materials", $"M_VideoLens3_{e.name}.mat");
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

                Undo.RecordObject(emr, "Apply Video Lens 3");
                AssignAll(emr, eMat);
                extraRenderers.Add(emr);
            }

            var vp = go.GetComponent<VideoPlayer>();
            if (vp == null) vp = Undo.AddComponent<VideoPlayer>(go);
            Undo.RecordObject(vp, "Apply Video Lens 3");
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
            Debug.Log($"[VideoTextureTest] Video lens 3 applied to '{go.name}' + [{string.Join(", ", extras.Select(x => x.name))}]. Sphere center {center}, radius {radius:0.###}, " +
                      $"window aimed {(front != null ? "at " + front.name : "at camera")}, surface from '{(original != null ? original.name : "defaults")}'.", go);
        }

        // ------------------------------------------------------------------
        // Version 4: like version 3, but the mesh keeps its ORIGINAL textured HDRP Lit surface
        // outside the window (Base Map + tiling, Base Color, Mask Map with Metallic/Smoothness/AO
        // remapping, Normal Map + scale), copied from its current material. Built for textured
        // meshes such as rocks. Versions 1 and 3 above are left unchanged.
        // ------------------------------------------------------------------

        [MenuItem("Tools/VideoTextureTest/Apply Video Lens To Selected Object 4")]
        public static void ApplyToSelected4()
        {
            var go = Selection.activeGameObject;
            if (go == null || go.GetComponent<MeshFilter>() == null || go.GetComponent<MeshRenderer>() == null)
            {
                EditorUtility.DisplayDialog("VideoTextureTest", "Select a GameObject with a MeshFilter and MeshRenderer (e.g. a rock).", "OK");
                return;
            }
            // Any other selected meshes become "extra parts" that continue the same lens/window.
            var extras = Selection.gameObjects.Where(g => g != go && g.GetComponent<MeshRenderer>() != null).ToList();
            ApplyTo4(go, extras);
        }

        /// <param name="go">The main shell (sphere-fit, owns the VideoPlayer).</param>
        /// <param name="extras">Other parts that should continue the effect seamlessly. If null/empty,
        /// a sibling named "Front_Part" (the ring that fills the material ball's opening) is used.</param>
        public static void ApplyTo4(GameObject go, System.Collections.Generic.List<GameObject> extras = null)
        {
            var shader = ToolPaths.FindShader("SG_VideoLens4");
            if (shader == null)
            {
                EditorUtility.DisplayDialog("VideoTextureTest", "Couldn't find the SG_VideoLens4 shader graph in the project.", "OK");
                return;
            }
            var clip = FindClip();
            ToolPaths.EnsureFolder("Materials");
            var rt = GetOrCreateRT(clip);
            var mr = go.GetComponent<MeshRenderer>();

            // Original surface: the renderer's current material. If it's already one of our video lens
            // materials, its surface settings are kept as they are (re-running doesn't reset your tweaks).
            Material original = mr.sharedMaterial;
            bool originalIsOurs = original != null && original.shader != null && original.shader.name.Contains("SG_VideoLens");

            string matPath = ToolPaths.AssetPath("Materials", $"M_VideoLens4_{go.name}.mat");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            bool isNew = mat == null;
            if (isNew)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, matPath);
            }

            if (original != null && original != mat) CopySurface(original, mat);   // textures, tiling, remapping

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
            mat.SetFloat("_MaskFrontCutoff", 1.0f);   // full wrap: no hard cutoff from behind (user-tuned)
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

            Undo.RecordObject(mr, "Apply Video Lens 4");
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
                string ePath = ToolPaths.AssetPath("Materials", $"M_VideoLens4_{e.name}.mat");
                var eMat = AssetDatabase.LoadAssetAtPath<Material>(ePath);
                if (eMat == null) { eMat = new Material(shader); AssetDatabase.CreateAsset(eMat, ePath); }

                // Each part keeps ITS OWN surface (its original material, or its previous v4 settings).
                var surfaceSource = new Material(eOriginal != null && eOriginal != eMat ? eOriginal : eMat);
                eMat.CopyPropertiesFromMaterial(mat);   // shared lens / window / video settings
                CopySurface(surfaceSource, eMat);
                Object.DestroyImmediate(surfaceSource);

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

                Undo.RecordObject(emr, "Apply Video Lens 4");
                AssignAll(emr, eMat);
                extraRenderers.Add(emr);
            }

            var vp = go.GetComponent<VideoPlayer>();
            if (vp == null) vp = Undo.AddComponent<VideoPlayer>(go);
            Undo.RecordObject(vp, "Apply Video Lens 4");
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
            Debug.Log($"[VideoTextureTest] Video lens 4 applied to '{go.name}' + [{string.Join(", ", extras.Select(x => x.name))}]. Sphere center {center}, radius {radius:0.###}, " +
                      $"window aimed {(front != null ? "at " + front.name : "at camera")}, surface from '{(original != null ? original.name : "defaults")}'{(originalIsOurs ? " (kept existing surface settings)" : "")}.", go);
        }

        // ------------------------------------------------------------------------------------
        // Version 5: like v4, but on a TRANSPARENT surface (clear glass / crystal shards). The original
        // material's opacity is kept outside the video window. Every selected object is set up on its
        // OWN: its own lens, render texture (RT_VideoLens5_[name]) and VideoPlayer, so each piece can
        // play a different video / offset (e.g. its own Timeline video track).
        // ------------------------------------------------------------------------------------
        [MenuItem("Tools/VideoTextureTest/Apply Video Lens To Selected Object 5")]
        public static void ApplyToSelected5()
        {
            var targets = Selection.gameObjects.Where(g => g.GetComponent<MeshRenderer>() != null && g.GetComponent<MeshFilter>() != null).ToList();
            if (targets.Count == 0)
            {
                EditorUtility.DisplayDialog("VideoTextureTest", "Select one or more GameObjects with a MeshFilter and MeshRenderer (e.g. glass shards). " +
                    "Each one gets its own lens, render texture and VideoPlayer.", "OK");
                return;
            }
            foreach (var t in targets) ApplyTo5(t);
        }

        /// <summary>Sets up one object: own lens (from its own mesh), own render texture, VideoPlayer and binder.</summary>
        public static void ApplyTo5(GameObject go)
        {
            var shader = ToolPaths.FindShader("SG_VideoLens5");
            if (shader == null)
            {
                EditorUtility.DisplayDialog("VideoTextureTest", "Couldn't find the SG_VideoLens5 shader graph in the project.", "OK");
                return;
            }
            var clip = FindClip();
            ToolPaths.EnsureFolder("Materials");
            var rt = GetOrCreateRT5(go.name, clip);
            var mr = go.GetComponent<MeshRenderer>();

            // Lens = this object's own mesh: bounds center, radius = farthest vertex.
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            var verts = mesh.vertices;
            var bounds = mesh.bounds;
            Vector3 center = bounds.center;
            float radius = 0f;
            foreach (var v in verts) radius = Mathf.Max(radius, (v - center).magnitude);
            radius = Mathf.Max(radius, 1e-4f);

            Vector3 lensF;
            var cam = GetAimCamera();
            if (cam != null) VideoRenderTextureBinder.ComputeLensBasis(go.transform, cam, out lensF, out _);
            else lensF = new Vector3(0, 0, -1);
            Vector3 lensU = go.transform.InverseTransformDirection(Vector3.up).normalized;

            var mat = SetupLens5Material(go, shader, rt, center, radius, lensF, lensU);
            Undo.RecordObject(mr, "Apply Video Lens 5");
            AssignAll(mr, mat);

            var vp = go.GetComponent<VideoPlayer>();
            if (vp == null) vp = Undo.AddComponent<VideoPlayer>(go);
            Undo.RecordObject(vp, "Apply Video Lens 5");
            vp.playOnAwake = false;
            vp.isLooping = true;
            vp.renderMode = VideoRenderMode.RenderTexture;
            vp.targetTexture = rt;
            vp.audioOutputMode = VideoAudioOutputMode.None;
            if (clip != null && vp.clip == null) vp.clip = clip;

            var binder = go.GetComponent<VideoRenderTextureBinder>();
            if (binder == null) binder = Undo.AddComponent<VideoRenderTextureBinder>(go);
            Undo.RecordObject(binder, "Apply Video Lens 5");
            binder.renderTexture = rt;
            binder.targetRenderer = mr;
            binder.aimLensAtCamera = true;
            binder.muteAudio = true;
            binder.extraRenderers = new Renderer[0];

            // Inside a prefab instance, make sure these count as overrides (otherwise the prefab's
            // old values can come back, e.g. a binder still pointing at a shared render texture).
            if (PrefabUtility.IsPartOfPrefabInstance(go))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(mr);
                PrefabUtility.RecordPrefabInstancePropertyModifications(vp);
                PrefabUtility.RecordPrefabInstancePropertyModifications(binder);
            }

            EditorUtility.SetDirty(go);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[VideoTextureTest] Video lens 5 (transparent) applied to '{go.name}': own render texture '{rt.name}', lens center {center}, radius {radius:0.###}.", go);
        }

        static RenderTexture GetOrCreateRT5(string objectName, VideoClip clip)
        {
            string path = ToolPaths.AssetPath("Materials", $"RT_VideoLens5_{objectName}.renderTexture");
            var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
            if (rt != null) return rt;
            int w = clip != null ? (int)clip.width : 1920;
            int h = clip != null ? (int)clip.height : 1080;
            rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                useMipMap = false, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
            };
            AssetDatabase.CreateAsset(rt, path);
            return rt;
        }

        /// <summary>Creates / updates M_VideoLens5_[name] for one piece, keeping its original (possibly transparent) surface.</summary>
        static Material SetupLens5Material(GameObject piece, Shader shader, RenderTexture rt, Vector3 center, float radius, Vector3 lensF, Vector3 lensU)
        {
            var r = piece.GetComponent<MeshRenderer>();
            Material original = r.sharedMaterial;
            string path = ToolPaths.AssetPath("Materials", $"M_VideoLens5_{piece.name}.mat");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool isNew = mat == null;
            if (isNew)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (mat.shader != shader) mat.shader = shader;

            // A duplicated piece still points at its source's v5 material: start from a full copy of it.
            bool originalIsV5 = original != null && original.shader != null && original.shader.name.Contains("SG_VideoLens5");
            if (isNew && originalIsV5 && original != mat) mat.CopyPropertiesFromMaterial(original);

            // Surface (incl. opacity) from the original material. Re-running keeps the v5 material's own surface.
            if (original != null && original != mat && !originalIsV5)
            {
                CopySurface(original, mat);
                mat.SetColor("_BaseColor", OriginalColorWithOpacity(original, mat.GetColor("_BaseColor")));
            }

            mat.SetTexture("_VideoTex", rt);
            mat.SetFloat("_MappingMode", 1f);
            mat.SetVector("_LensCenter", center);
            mat.SetFloat("_MeshRadius", radius);
            mat.SetFloat("_ProjectionSize", radius * 2f);
            mat.SetFloat("_InteriorDepth", radius * 0.6f);
            mat.SetVector("_LensForward", lensF);
            mat.SetVector("_LensUp", lensU);
            if (isNew && !originalIsV5)
            {
                // Window covers the whole piece; the surface still shows at the edges.
                mat.SetVector("_MaskForward", lensF);
                mat.SetVector("_MaskUp", lensU);
                mat.SetFloat("_MaskRadius", 1.15f);
                mat.SetFloat("_MaskSoftness", 0.35f);
                mat.SetFloat("_FadeCurve", 0.6f);
                mat.SetFloat("_VideoSpread", 2.0f);
                mat.SetFloat("_MaskFrontCutoff", 1.0f);
                mat.SetFloat("_VideoIntensity", 3.0f);
                mat.SetFloat("_Vignette", 0.35f);
                mat.SetFloat("_MaskDebug", 0f);
                ApplyClearGlassPreset(mat);
            }
            UnityEngine.Rendering.HighDefinition.HDMaterial.ValidateMaterial(mat);   // transparent keywords / render queue
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>Clear, see-through glass: light tint inside the window, low opacity, video carries the image.</summary>
        public static void ApplyClearGlassPreset(Material mat)
        {
            mat.SetColor("_GlassColor", new Color(0.85f, 0.92f, 1f, 1f));
            mat.SetFloat("_GlassSmoothness", 0.97f);
            mat.SetFloat("_GlassMetallic", 0f);
            mat.SetFloat("_KeepSurfaceInWindow", 0f);
            mat.SetFloat("_WindowOpacity", 0.05f);
            mat.SetFloat("_FootageOpacity", 0.55f);
            mat.SetFloat("_EdgeOpacity", 0.04f);
            mat.SetFloat("_Grain", 0f);
            mat.SetFloat("_Vignette", 0.1f);                                 // no dark band along the outline
            mat.SetColor("_FresnelColor", new Color(0.08f, 0.1f, 0.13f, 1f)); // faint rim glow only
            var bc = mat.GetColor("_BaseColor"); bc.a = Mathf.Min(bc.a, 0.04f); mat.SetColor("_BaseColor", bc);
        }

        /// <summary>Base color with the original material's real opacity (opaque materials count as fully opaque).</summary>
        static Color OriginalColorWithOpacity(Material src, Color c)
        {
            bool transparent =
                (src.HasProperty("_SurfaceType") && src.GetFloat("_SurfaceType") > 0.5f) ||   // HDRP
                (src.HasProperty("_Surface") && src.GetFloat("_Surface") > 0.5f) ||           // URP
                (src.HasProperty("_Mode") && src.GetFloat("_Mode") > 1.5f) ||                 // Built-in Standard (Fade / Transparent)
                src.renderQueue >= 2501;
            if (!transparent) c.a = 1f;
            return c;
        }

        // HDRP Lit-style surface properties shared with SG_VideoLens4 (same reference names).
        static readonly string[] SurfaceFloats =
        {
            "_NormalScale", "_Metallic", "_Smoothness",
            "_MetallicRemapMin", "_MetallicRemapMax", "_SmoothnessRemapMin", "_SmoothnessRemapMax",
            "_AORemapMin", "_AORemapMax",
        };

        /// <summary>Copies a Lit-style surface (HDRP Lit, URP Lit/Standard names, or another v4 material) onto a v4 material.</summary>
        static void CopySurface(Material src, Material dst)
        {
            if (src.HasProperty("_OutsideColor"))
            {
                // A version 3 material: flat "outside" surface (no textures).
                dst.SetColor("_BaseColor", src.GetColor("_OutsideColor"));
                dst.SetTexture("_BaseColorMap", null);
                dst.SetTexture("_MaskMap", null);
                dst.SetTexture("_NormalMap", null);
                dst.SetFloat("_UseMaskMap", 0f);
                if (src.HasProperty("_OutsideMetallic")) dst.SetFloat("_Metallic", src.GetFloat("_OutsideMetallic"));
                if (src.HasProperty("_OutsideSmoothness")) dst.SetFloat("_Smoothness", src.GetFloat("_OutsideSmoothness"));
                return;
            }
            string baseProp = src.HasProperty("_BaseColorMap") ? "_BaseColorMap" : src.HasProperty("_BaseMap") ? "_BaseMap" : src.HasProperty("_MainTex") ? "_MainTex" : null;
            if (baseProp != null)
            {
                dst.SetTexture("_BaseColorMap", src.GetTexture(baseProp));
                dst.SetTextureScale("_BaseColorMap", src.GetTextureScale(baseProp));
                dst.SetTextureOffset("_BaseColorMap", src.GetTextureOffset(baseProp));
            }
            if (src.HasProperty("_BaseColor")) dst.SetColor("_BaseColor", src.GetColor("_BaseColor"));
            else if (src.HasProperty("_Color")) dst.SetColor("_BaseColor", src.GetColor("_Color"));

            var mask = src.HasProperty("_MaskMap") ? src.GetTexture("_MaskMap") : null;
            dst.SetTexture("_MaskMap", mask);
            dst.SetFloat("_UseMaskMap", mask != null ? 1f : 0f);   // like HDRP Lit: remapping only applies with a mask map

            string nProp = src.HasProperty("_NormalMap") ? "_NormalMap" : src.HasProperty("_BumpMap") ? "_BumpMap" : null;
            dst.SetTexture("_NormalMap", nProp != null ? src.GetTexture(nProp) : null);
            if (!src.HasProperty("_NormalScale") && src.HasProperty("_BumpScale")) dst.SetFloat("_NormalScale", src.GetFloat("_BumpScale"));

            foreach (var f in SurfaceFloats)
                if (src.HasProperty(f)) dst.SetFloat(f, src.GetFloat(f));
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

        [MenuItem("Tools/VideoTextureTest/Create Video Lens Demo")]
        public static void CreateDemo()
        {
            var shader = ToolPaths.FindShader("SG_VideoLens");
            if (shader == null)
            {
                EditorUtility.DisplayDialog("VideoTextureTest", "Couldn't find the SG_VideoLens shader graph in the project. Let Unity finish importing and try again.", "OK");
                return;
            }

            // Video clip: selected clip > first clip in the project.
            var clip = Selection.activeObject as VideoClip
                       ?? AssetDatabase.FindAssets("t:VideoClip").Select(g => AssetDatabase.LoadAssetAtPath<VideoClip>(AssetDatabase.GUIDToAssetPath(g))).FirstOrDefault();

            ToolPaths.EnsureFolder("Materials");

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
            go.name = "Video Lens";
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
                // tilted a little for a more dynamic angle.
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

            Undo.RegisterCreatedObjectUndo(go, "Create Video Lens");
            Selection.activeGameObject = go;
            AssetDatabase.SaveAssets();

            if (clip == null)
                Debug.LogWarning("[VideoTextureTest] No VideoClip found. Assign one on the VideoPlayer of 'Video Lens'.");
            else
                Debug.Log($"[VideoTextureTest] Video lens created with clip '{clip.name}'. Press Play.");
        }
    }
}
