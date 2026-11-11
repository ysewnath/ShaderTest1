using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace VideoTextureTest.EditorTools
{
    public static class OutlineHighlightSetup
    {
        const string VolumeName = "Outline Highlight Pass";

        [MenuItem("Tools/VideoTextureTest/Add Outline Highlight To Selected")]
        static void AddToSelected()
        {
            var targets = Selection.gameObjects;
            if (targets.Length == 0)
            {
                EditorUtility.DisplayDialog("VideoTextureTest", "Select the object(s) to outline first.", "OK");
                return;
            }
            EnsurePassVolume();
            foreach (var go in targets)
            {
                if (go.GetComponent<VideoTextureTest.OutlineHighlight>() == null)
                    Undo.AddComponent<VideoTextureTest.OutlineHighlight>(go);
            }
        }

        /// <summary>Finds or creates the global Custom Pass Volume that draws the outlines.</summary>
        public static CustomPassVolume EnsurePassVolume()
        {
            var vol = Object.FindObjectsByType<CustomPassVolume>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(v => v.customPasses.Any(p => p is VideoTextureTest.OutlineHighlightPass));
            if (vol != null) return vol;

            var go = new GameObject(VolumeName);
            Undo.RegisterCreatedObjectUndo(go, "Create Outline Highlight Pass");
            vol = go.AddComponent<CustomPassVolume>();
            vol.isGlobal = true;
            vol.injectionPoint = CustomPassInjectionPoint.BeforePostProcess;   // after the water and transparents
            var pass = (VideoTextureTest.OutlineHighlightPass)vol.AddPassOfType(typeof(VideoTextureTest.OutlineHighlightPass));
            pass.name = "Outline Highlight";
            pass.SetShaders(Shader.Find("Hidden/VideoTextureTest/OutlineMask"), Shader.Find("Hidden/VideoTextureTest/OutlineComposite"));
            EditorUtility.SetDirty(vol);
            return vol;
        }
    }
}
