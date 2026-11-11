using System.Collections.Generic;
using UnityEngine;

namespace VideoTextureTest
{
    /// <summary>
    /// Marks an object (and optionally its children) to be outlined by <see cref="OutlineHighlightPass"/>.
    /// The outline is drawn as a screen-space effect, so nothing is rendered on top of the object itself.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class OutlineHighlight : MonoBehaviour
    {
        public static readonly List<OutlineHighlight> Active = new List<OutlineHighlight>();

        [Tooltip("Outline child renderers too (e.g. a model made of several parts).")]
        public bool includeChildren = true;

        readonly List<Renderer> _renderers = new List<Renderer>();
        public IReadOnlyList<Renderer> Renderers => _renderers;

        void OnEnable()
        {
            Refresh();
            if (!Active.Contains(this)) Active.Add(this);
        }

        void OnDisable() => Active.Remove(this);

        void OnTransformChildrenChanged() => Refresh();

        void OnValidate() => Refresh();

        /// <summary>Re-collects the renderers (call after adding / removing parts at runtime).</summary>
        public void Refresh()
        {
            _renderers.Clear();
            if (includeChildren) GetComponentsInChildren(true, _renderers);
            else { var r = GetComponent<Renderer>(); if (r != null) _renderers.Add(r); }
            // Particles / trails / lines are effects, not the object's silhouette.
            _renderers.RemoveAll(r => r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer);
        }
    }
}
