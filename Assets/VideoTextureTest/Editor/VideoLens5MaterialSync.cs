using UnityEditor;
using UnityEngine;

namespace VideoTextureTest.EditorTools
{
    /// <summary>
    /// HDRP only copies some Shader Graph surface settings into a material when the material is
    /// created, so materials made before a graph change keep the old values. Whenever
    /// SG_VideoLens5 is (re)imported, this resets those settings on every material that uses it
    /// so they match the graph (e.g. Preserve Specular Lighting off, which lets the glass
    /// reflections fade with opacity and Reveal instead of leaving the crystal edges visible).
    /// </summary>
    class VideoLens5MaterialSync : AssetPostprocessor
    {
        const string GraphName = "SG_VideoLens5";
        static readonly string[] k_SyncedFloats = { "_EnableBlendModePreserveSpecularLighting" };

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var path in imported)
            {
                if (path.EndsWith(".shadergraph") && System.IO.Path.GetFileNameWithoutExtension(path) == GraphName)
                {
                    EditorApplication.delayCall += () => SyncAll(path);
                    return;
                }
            }
        }

        static void SyncAll(string graphPath)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(graphPath);
            if (shader == null) return;
            int count = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (mat == null || mat.shader != shader) continue;
                bool changed = false;
                foreach (var prop in k_SyncedFloats)
                {
                    int i = shader.FindPropertyIndex(prop);
                    if (i < 0) continue;
                    float def = shader.GetPropertyDefaultFloatValue(i);
                    if (!Mathf.Approximately(mat.GetFloat(prop), def)) { mat.SetFloat(prop, def); changed = true; }
                }
                if (!changed) continue;
                UnityEngine.Rendering.HighDefinition.HDMaterial.ValidateMaterial(mat);
                EditorUtility.SetDirty(mat);
                count++;
            }
            if (count > 0)
            {
                AssetDatabase.SaveAssets();
                Debug.Log($"[VideoTextureTest] Synced {count} Video Lens 5 material(s) with {GraphName}.");
            }
        }
    }
}
