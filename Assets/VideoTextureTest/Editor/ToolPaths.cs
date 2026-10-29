using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VideoTextureTest.EditorTools
{
    /// <summary>
    /// Resolves where the tool lives at runtime instead of hardcoding "Assets/...".
    /// The tool root is the folder that contains this script's Editor folder, so the whole
    /// tool folder can be moved or renamed anywhere in the project. Assets (shaders,
    /// textures, materials) are found by name inside the root first, then anywhere in the project.
    /// </summary>
    public static class ToolPaths
    {
        const string ShaderMenuPath = "VideoTextureTest";   // Shader Graph "path" of the tool's shaders

        /// <summary>The tool's root folder, e.g. "Assets/VideoTextureTest" (wherever it currently is).</summary>
        public static string Root
        {
            get
            {
                foreach (var guid in AssetDatabase.FindAssets("ToolPaths t:MonoScript"))
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.Equals(Path.GetFileName(p), "ToolPaths.cs", StringComparison.OrdinalIgnoreCase)) continue;
                    var editorDir = Path.GetDirectoryName(p)?.Replace('\\', '/');
                    var root = Path.GetDirectoryName(editorDir ?? "")?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(root) && AssetDatabase.IsValidFolder(root)) return root;
                }
                return "Assets";
            }
        }

        /// <summary>Returns Root/sub, creating it if needed.</summary>
        public static string EnsureFolder(string sub)
        {
            string root = Root;
            string path = root + "/" + sub;
            if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(root, sub);
            return path;
        }

        /// <summary>Path of an existing asset with this exact file name (searched under searchRoot, or the whole project), else null.</summary>
        public static string FindPath(string fileName, string searchRoot = null)
        {
            string name = Path.GetFileNameWithoutExtension(fileName);
            var guids = searchRoot != null && AssetDatabase.IsValidFolder(searchRoot)
                ? AssetDatabase.FindAssets(name, new[] { searchRoot })
                : AssetDatabase.FindAssets(name);
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        /// <summary>Where an asset of the tool lives: its current location if it exists anywhere in the tool, else Root/sub/fileName.</summary>
        public static string AssetPath(string sub, string fileName)
            => FindPath(fileName, Root) ?? EnsureFolder(sub) + "/" + fileName;

        /// <summary>Loads an asset by file name (tool folder first, then the whole project).</summary>
        public static T Load<T>(string fileName) where T : UnityEngine.Object
        {
            var p = FindPath(fileName, Root) ?? FindPath(fileName);
            return p != null ? AssetDatabase.LoadAssetAtPath<T>(p) : null;
        }

        /// <summary>Finds one of the tool's Shader Graphs by name (e.g. "SG_VideoLens"), wherever it is.</summary>
        public static Shader FindShader(string graphName)
        {
            var s = Shader.Find(ShaderMenuPath + "/" + graphName);
            return s != null ? s : Load<Shader>(graphName + ".shadergraph");
        }
    }
}
