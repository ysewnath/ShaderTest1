using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace VideoTextureTest.EditorTools
{
    /// <summary>Creates a glowing-orange emissive logo decal (logo projected onto hardware as red-hot light).</summary>
    public static class GlowDecalSetup
    {
        /// <summary>
        /// Select the hardware to project onto (optional) and, optionally, a logo texture in the
        /// Project window, then run this. The decal projects along the Scene view camera's direction.
        /// </summary>
        [MenuItem("Tools/VideoTextureTest/Create Glow Decal On Selected")]
        public static void CreateGlowDecal()
        {
            var shader = ToolPaths.FindShader("SG_GlowDecal");
            if (shader == null)
            {
                EditorUtility.DisplayDialog("VideoTextureTest", "Couldn't find the SG_GlowDecal shader graph in the project. Let Unity finish importing.", "OK");
                return;
            }

            // Logo: a texture selected in the Project window, else the placeholder.
            Texture2D logo = null;
            foreach (var o in Selection.objects) if (o is Texture2D t) { logo = t; break; }
            var placeholder = ToolPaths.Load<Texture2D>("T_GlowLogo_Placeholder.png");
            if (logo == null) logo = placeholder;

            string matPath = logo != null && logo != placeholder
                ? ToolPaths.AssetPath("Materials", $"M_GlowDecal_{logo.name}.mat")
                : ToolPaths.AssetPath("Materials", "M_GlowDecal.mat");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, matPath);
            }
            if (logo != null) mat.SetTexture("_LogoTex", logo);
            EditorUtility.SetDirty(mat);

            // Projection direction: Scene view camera (what you're looking at), else Main Camera.
            Transform cam = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera.transform
                          : (Camera.main != null ? Camera.main.transform : null);
            Vector3 fwd = cam != null ? cam.forward : Vector3.forward;
            Vector3 up = cam != null ? cam.up : Vector3.up;

            float aspect = logo != null && logo.width > 0 ? (float)logo.height / logo.width : 0.25f;
            var target = Selection.activeGameObject;
            Renderer r = target != null ? target.GetComponentInChildren<Renderer>() : null;

            Vector3 pos; Vector3 size;
            if (r != null)
            {
                // Cover the visible front half of the selected object.
                var b = r.bounds;
                foreach (var cr in target.GetComponentsInChildren<Renderer>()) b.Encapsulate(cr.bounds);
                float rad = b.extents.magnitude;
                float width = rad * 1.4f;
                size = new Vector3(width, width * aspect, rad * 1.2f);
                pos = b.center - fwd * rad;
            }
            else
            {
                Vector3 p0 = cam != null ? cam.position + cam.forward * 2f : Vector3.zero;
                size = new Vector3(1f, aspect, 1f);
                pos = p0 - fwd * 0.5f;
            }

            var go = new GameObject("Glow Decal");
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(fwd, up));
            var proj = go.AddComponent<DecalProjector>();
            proj.material = mat;
            proj.size = size;
            proj.pivot = new Vector3(0f, 0f, size.z * 0.5f);   // box starts at the GameObject and extends forward
            proj.drawDistance = 1000f;
            go.AddComponent<VideoTextureTest.GlowDecalAnimator>();

            if (target != null) go.transform.SetParent(target.transform, true);   // moves with the hardware
            Undo.RegisterCreatedObjectUndo(go, "Create Glow Decal");
            Selection.activeGameObject = go;
            AssetDatabase.SaveAssets();
            Debug.Log($"[VideoTextureTest] Glow decal created{(target != null ? " on '" + target.name + "'" : "")} with logo '{(logo != null ? logo.name : "none")}'. " +
                      "Move/rotate/scale it like any object; press Play to see the ignite.", go);
        }
    }
}
