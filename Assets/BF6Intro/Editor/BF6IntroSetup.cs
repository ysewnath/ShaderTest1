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
