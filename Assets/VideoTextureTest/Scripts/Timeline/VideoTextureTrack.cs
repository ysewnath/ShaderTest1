using System;
using System.Collections.Generic;
using System.ComponentModel;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityEngine.Video;

namespace VideoTextureTest.TimelineTracks
{
    // ------------------------------------------------------------------------
    // VideoTextureTest / Video Player Track
    // Bind it to the object with the VideoPlayer that feeds your RenderTexture
    // (e.g. the rock / Ball_Part). Each clip = "the video plays here":
    //   - starts at the clip's start, from the clip's Clip In point in the video
    //   - stops (pauses on its frame) when the playhead leaves the clip
    //   - Timeline playback: plays at normal speed and seeks back in step whenever it drifts
    //     from the Timeline by more than a few frames (loops, skips, playhead moves)
    //   - Scrubbing: seeks to (approximately) the frame under the playhead
    // The track takes playback over from the VideoRenderTextureBinder's Auto Play.
    // Based on Unity's Timeline "Video" customization sample, retargeted to an
    // existing VideoPlayer + RenderTexture. Unity's VideoPlayer seeks asynchronously,
    // so scrubbing is approximate (it lands a moment later), not frame-exact.
    // ------------------------------------------------------------------------
    [Serializable]
    [TrackColor(0.008f, 0.698f, 0.655f)]
    [TrackClipType(typeof(VideoTextureClip))]
    [TrackBindingType(typeof(VideoPlayer))]
    [DisplayName("VideoTextureTest/Video Player Track")]
    public class VideoTextureTrack : TrackAsset
    {
        [Tooltip("Never drop video frames during playback (smoother Recorder renders; may run slower than real time).")]
        public bool frameAccurate = true;

        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
        {
            foreach (var clip in GetClips())
            {
                if (clip.asset is VideoTextureClip a)
                {
                    a.clipInTime = clip.clipIn;
                    a.startTime = clip.start;
                }
            }
            var mixer = ScriptPlayable<VideoTextureMixerBehaviour>.Create(graph, inputCount);
            mixer.GetBehaviour().frameAccurate = frameAccurate;
            return mixer;
        }
    }

    public sealed class VideoTextureClipBehaviour : PlayableBehaviour
    {
        public VideoTextureClip asset;
        public double startTime;
        public double clipInTime;
    }

    public sealed class VideoTextureMixerBehaviour : PlayableBehaviour
    {
        public bool frameAccurate = true;
        VideoPlayer _player;
        double _lastLocal = double.NaN;
        double _lastScrubTarget = double.NaN;   // last frame we asked for while scrubbing
        float _driftCheckResumeTime;            // realtime until which we wait for a seek to land

        // How far the video may drift from the Timeline before we seek (instead of letting it race to catch up).
        const double MaxDriftSeconds = 0.12;

        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            var vp = playerData as VideoPlayer;
            if (vp == null) return;
            _player = vp;

            // The Timeline owns playback: stop the binder's own Auto Play from starting the video.
            var binder = vp.GetComponent<VideoRenderTextureBinder>();
            if (binder != null) binder.HandOffPlaybackToTimeline();

            // Strongest active clip.
            int best = -1; float bestW = 0f;
            for (int i = 0; i < playable.GetInputCount(); i++)
            {
                float w = playable.GetInputWeight(i);
                if (w > bestW) { bestW = w; best = i; }
            }

            if (best < 0)
            {
                // No clip under the playhead: preload an upcoming clip, otherwise stop (hold the last frame).
                Preload(playable, vp);
                vp.timeReference = VideoTimeReference.Freerun;
                EditModeVideoPump.HoldPaused(vp);   // Edit mode: make sure it really stays stopped
                if (vp.isPlaying) vp.Pause();
                _lastLocal = double.NaN;
                _lastScrubTarget = double.NaN;
                return;
            }

            var input = (ScriptPlayable<VideoTextureClipBehaviour>)playable.GetInput(best);
            var b = input.GetBehaviour();
            if (b == null || b.asset == null) return;
            var a = b.asset;

            var clip = a.videoClip != null ? a.videoClip : vp.clip;
            if (clip == null) return;
            if (vp.source != VideoSource.VideoClip) vp.source = VideoSource.VideoClip;
            if (vp.clip != clip) vp.clip = clip;
            vp.playOnAwake = false;
            vp.isLooping = a.loop;
            vp.skipOnDrop = !frameAccurate;
            if (a.mute && vp.audioOutputMode != VideoAudioOutputMode.None) vp.audioOutputMode = VideoAudioOutputMode.None;

            if (!vp.isPrepared)
            {
                vp.Prepare();
                EditModeVideoPump.Kick(vp);
                return;   // starts on a following evaluation once it's ready
            }

            double local = input.GetTime();               // seconds into the video (includes Clip In and Speed)
            double len = clip.length;
            bool playing = info.evaluationType == FrameData.EvaluationType.Playback;
            if (!a.loop && local >= len) playing = false;  // past the end of a non-looping video: hold

            if (playing)
            {
                // Play at normal speed on game time (fixed under the Recorder) and seek whenever the video is off
                // from the Timeline by more than a few frames - e.g. the Timeline looped, skipped, or was moved.
                // (VideoTimeReference.ExternalTime is NOT used: it speeds the video up/down to catch up instead.)
                EditModeVideoPump.CancelScrub(vp);
                if (vp.playbackSpeed <= 0f) vp.playbackSpeed = 1f;
                vp.timeReference = VideoTimeReference.Freerun;
                vp.timeUpdateMode = VideoTimeUpdateMode.GameTime;
                _lastScrubTarget = double.NaN;
                double t = Wrap(local, a.loop, len);
                bool jumpedBack = !double.IsNaN(_lastLocal) && local < _lastLocal - 1e-3;
                float now = Time.realtimeSinceStartup;
                if (!vp.isPlaying)
                {
                    vp.time = t;
                    vp.Play();
                    _driftCheckResumeTime = now + 0.5f;   // let the seek land before judging drift
                }
                else if (jumpedBack)
                {
                    // Timeline looped / jumped back: seek once, then wait for it to land.
                    vp.time = t;
                    _driftCheckResumeTime = now + 0.5f;
                }
                else if (now >= _driftCheckResumeTime)
                {
                    // vp.time reports the old position until a seek completes, so only check when no seek is pending.
                    double drift = Math.Abs(vp.time - t);
                    if (a.loop && len > 0) drift = Math.Min(drift, len - drift);   // wrapping isn't drift
                    double maxDrift = Math.Max(MaxDriftSeconds, vp.frameRate > 0 ? 3.0 / vp.frameRate : 0.0);
                    if (drift > maxDrift)
                    {
                        vp.time = t;
                        _driftCheckResumeTime = now + 0.5f;
                    }
                }
                _lastLocal = local;
                EditModeVideoPump.Kick(vp);   // Edit-mode Timeline preview playback
            }
            else
            {
                // Scrubbing / paused: go to (about) the frame under the playhead, then stay paused.
                // Only react when the PLAYHEAD moves (compare with the last requested frame, not vp.time,
                // which changes while we briefly play to fetch the frame).
                _lastLocal = double.NaN;
                vp.timeReference = VideoTimeReference.Freerun;
                double t = Wrap(local, a.loop, len);
                double halfFrame = vp.frameRate > 0 ? 0.5 / vp.frameRate : 1.0 / 60.0;
                if (double.IsNaN(_lastScrubTarget) || Math.Abs(t - _lastScrubTarget) > halfFrame)
                {
                    _lastScrubTarget = t;
                    vp.time = t;
                    if (Application.isPlaying) { if (!vp.isPaused) vp.Pause(); }
                    else EditModeVideoPump.ScrubTo(vp, t);   // Edit mode: frozen at speed 0 on that frame
                }
                else if (Application.isPlaying && vp.isPlaying)
                {
                    vp.Pause();   // Play mode with the Timeline paused: hold the frame
                }
            }
        }

        static double Wrap(double local, bool loop, double len)
            => len > 0 ? (loop ? local % len : Math.Min(local, len)) : local;

        void Preload(Playable playable, VideoPlayer vp)
        {
            double now = playable.GetGraph().GetRootPlayable(0).GetTime();
            for (int i = 0; i < playable.GetInputCount(); i++)
            {
                var input = (ScriptPlayable<VideoTextureClipBehaviour>)playable.GetInput(i);
                var b = input.GetBehaviour();
                if (b == null || b.asset == null) continue;
                double pre = Math.Max(0.0, b.asset.preloadTime);
                if (now > b.startTime - pre && now <= b.startTime)
                {
                    var clip = b.asset.videoClip != null ? b.asset.videoClip : vp.clip;
                    if (clip == null) return;
                    if (vp.clip != clip) vp.clip = clip;
                    if (!vp.isPrepared) { vp.Prepare(); EditModeVideoPump.Kick(vp); }
                    return;
                }
            }
        }

        public override void OnGraphStop(Playable playable) => Release();
        public override void OnPlayableDestroy(Playable playable) => Release();

        void Release()
        {
            if (_player == null) return;
            EditModeVideoPump.HoldPaused(_player);
            _player.timeReference = VideoTimeReference.Freerun;
            if (_player.isPlaying) _player.Pause();
            _lastLocal = double.NaN;
            _lastScrubTarget = double.NaN;
        }
    }

    // ------------------------------------------------------------------------
    // Edit mode helper. Outside Play mode a VideoPlayer only decodes frames when the player
    // loop runs, and a PAUSED player often doesn't show a frame it seeked to, while a playing
    // one runs away. So in Edit mode a scrubbed player is "frozen": playing at playbackSpeed 0.
    // It keeps decoding/presenting whatever frame it's moved to, but its clock can't advance.
    // A guard re-applies the freeze every editor update until Timeline playback or Play mode
    // takes over (which restores normal speed). Nothing runs in Play mode or in builds.
    // ------------------------------------------------------------------------
    public static class EditModeVideoPump
    {
#if UNITY_EDITOR
        static readonly HashSet<VideoPlayer> s_Frozen = new HashSet<VideoPlayer>();
        static bool s_Running, s_GuardInstalled;
        static int s_TicksLeft;
#endif

        /// <summary>Keep the editor ticking/repainting for a moment so the video decodes and the views update.</summary>
        public static void Kick(VideoPlayer vp)
        {
#if UNITY_EDITOR
            if (Application.isPlaying || vp == null) return;
            s_TicksLeft = 60;   // ~1 s of editor ticks
            if (!s_Running)
            {
                s_Running = true;
                UnityEditor.EditorApplication.update += Tick;
            }
#endif
        }

        /// <summary>Edit mode: show (about) the frame at <paramref name="time"/> and stay frozen there.</summary>
        public static void ScrubTo(VideoPlayer vp, double time)
        {
#if UNITY_EDITOR
            if (Application.isPlaying || vp == null) return;
            Freeze(vp);
            vp.time = time;
            Kick(vp);
#endif
        }

        /// <summary>Edit mode: keep this player stopped on its current frame.</summary>
        public static void HoldPaused(VideoPlayer vp)
        {
#if UNITY_EDITOR
            if (Application.isPlaying || vp == null) return;
            Freeze(vp);
#endif
        }

        public static bool IsScrubbing(VideoPlayer vp)
        {
#if UNITY_EDITOR
            return vp != null && s_Frozen.Contains(vp);
#else
            return false;
#endif
        }

        /// <summary>Timeline playback / Play mode takes the player over: unfreeze (normal speed).</summary>
        public static void CancelScrub(VideoPlayer vp)
        {
#if UNITY_EDITOR
            if (vp == null) return;
            if (s_Frozen.Remove(vp) && vp.playbackSpeed <= 0f) vp.playbackSpeed = 1f;
#endif
        }

#if UNITY_EDITOR
        static void Freeze(VideoPlayer vp)
        {
            InstallGuard();
            s_Frozen.Add(vp);
            if (vp.canSetPlaybackSpeed)
            {
                if (vp.playbackSpeed != 0f) vp.playbackSpeed = 0f;
                if (!vp.isPlaying) vp.Play();      // "playing" at speed 0 = shows seeks, never advances
            }
            else if (vp.isPlaying)
            {
                vp.Pause();                        // fallback for players that can't change speed
            }
        }

        static void InstallGuard()
        {
            if (s_GuardInstalled) return;
            s_GuardInstalled = true;
            UnityEditor.EditorApplication.update += Guard;
            UnityEditor.EditorApplication.playModeStateChanged += _ => RestoreAll();
        }

        // Every editor update: frozen players stay frozen, whatever else touched them.
        static void Guard()
        {
            if (Application.isPlaying) { RestoreAll(); return; }
            s_Frozen.RemoveWhere(p => p == null);
            foreach (var p in s_Frozen)
            {
                if (p.canSetPlaybackSpeed) { if (p.playbackSpeed != 0f) p.playbackSpeed = 0f; }
                else if (p.isPlaying) p.Pause();
            }
        }

        static void RestoreAll()
        {
            foreach (var p in s_Frozen)
                if (p != null && p.playbackSpeed <= 0f) p.playbackSpeed = 1f;
            s_Frozen.Clear();
        }

        static void Tick()
        {
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            if (--s_TicksLeft <= 0 || Application.isPlaying)
            {
                s_Running = false;
                UnityEditor.EditorApplication.update -= Tick;
            }
        }
#endif
    }
}
