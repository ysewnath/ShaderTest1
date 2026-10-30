using System;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityEngine.Video;

namespace VideoTextureTest.TimelineTracks
{
    // A clip on the "VideoTextureTest / Video Player Track": the bound VideoPlayer plays here,
    // locked to the Timeline clock. (Own file: Unity can only load a PlayableAsset whose class
    // name matches its file name.)
    [Serializable]
    public class VideoTextureClip : PlayableAsset, ITimelineClipAsset
    {
        [Tooltip("Optional: play this clip instead of the VideoPlayer's own clip.")]
        public VideoClip videoClip;

        [Tooltip("Loop the video when the Timeline clip is longer than the video.")]
        public bool loop = true;

        [Tooltip("Play without sound.")]
        public bool mute = true;

        [Tooltip("Seconds before the clip starts to begin loading the video, so it starts on time.")]
        public double preloadTime = 0.3;

        // Set by the track right before CreatePlayable (used for preloading).
        [NonSerialized] public double clipInTime;
        [NonSerialized] public double startTime;

        public override double duration => videoClip != null ? videoClip.length : base.duration;

        public ClipCaps clipCaps
        {
            get
            {
                var caps = ClipCaps.Blending | ClipCaps.ClipIn | ClipCaps.SpeedMultiplier;
                if (loop) caps |= ClipCaps.Looping;
                return caps;
            }
        }

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var p = ScriptPlayable<VideoTextureClipBehaviour>.Create(graph);
            var b = p.GetBehaviour();
            b.asset = this;
            b.startTime = startTime;
            b.clipInTime = clipInTime;
            return p;
        }
    }
}
