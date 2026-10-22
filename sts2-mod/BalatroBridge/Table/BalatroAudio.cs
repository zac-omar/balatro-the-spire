using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Godot;
using MegaCrit.Sts2.Core.Saves;

namespace BalatroBridge.Table;

/// <summary>
/// Balatro's soundtrack and sounds, played by StS2 itself from the files tools/build_balatro.py
/// extracts next to the bridged exe (the hidden engine stays silent). Playing them in StS2's
/// process means they come out wherever StS2's audio goes, including another output device or
/// Windows' per-app routing, and they sit on Godot's master bus, which StS2 keeps at its master
/// volume and mutes in the background.
///
/// Like Balatro, the music layers all play in sync and crossfade: the main theme, the shop and
/// the boss theme are the same song arranged differently. Balatro plays them slowed to 0.7x.
/// Call from the Godot main thread (except <see cref="Preload"/>).
/// </summary>
internal sealed class BalatroAudio
{
    public enum Track
    {
        Main = 0,
        Pack = 1, // Arcana, Spectral, Standard and Buffoon packs
        Celestial = 2, // Celestial packs
        Shop = 3,
        Boss = 4,
    }

    private static readonly string[] Layers = ["music1", "music2", "music3", "music4", "music5"];
    private const float MusicPitch = 0.7f;   // Balatro's own playback settings for its tracks
    private const float MusicVolume = 0.6f;
    private const float LayerFadeRate = 3f;  // Balatro moves layer volumes dt*3 towards their target each frame

    private static BalatroAudio? _instance;
    private static readonly ConcurrentDictionary<string, Lazy<AudioStream?>> Streams = new();

    private readonly Node _holder;
    private readonly AudioStreamPlayer[] _layers = new AudioStreamPlayer[Layers.Length];
    private readonly float[] _layerLevels = new float[Layers.Length];
    private readonly List<AudioStreamPlayer> _idle = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Track _track = Track.Main;
    private float _music;  // the overall fade, 0..1

    private bool _musicOn;
    private bool _started;
    private Tween? _fade;

    private BalatroAudio()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        _holder = new Node { Name = "BalatroAudio", ProcessMode = Node.ProcessModeEnum.Always };
        tree.Root.AddChild(_holder);
        for (int i = 0; i < Layers.Length; i++)
        {
            _layers[i] = new AudioStreamPlayer { Stream = Stream(Layers[i]), PitchScale = MusicPitch, VolumeDb = -80 };
            _holder.AddChild(_layers[i]);
        }
        _layerLevels[(int)Track.Main] = 1;
        tree.ProcessFrame += Update;
    }

    /// <summary>The player, set up on first use.</summary>
    public static BalatroAudio Instance =>
        _instance != null && GodotObject.IsInstanceValid(_instance._holder) ? _instance : _instance = new BalatroAudio();

    /// <summary>Loads the music and the given sounds. Call on a worker thread, ahead of use.</summary>
    public static void Preload(IEnumerable<string> sounds)
    {
        foreach (string layer in Layers) Stream(layer);
        foreach (string sound in sounds) Stream(sound);
    }

    /// <summary>Which arrangement plays; Balatro-style, the others fade out under it.</summary>
    public void SetTrack(Track track) => _track = track;

    /// <summary>
    /// Fades the soundtrack in or out on an equal-power curve: in, it swells quickly and settles;
    /// out, it eases off before dropping away. Faded out, it pauses where it was, as a radio would.
    /// </summary>
    public void FadeMusic(bool on, double seconds)
    {
        _musicOn = on;
        if (on) StartLayers();
        _fade?.Kill();
        _fade = _holder.CreateTween();
        _fade.TweenMethod(Callable.From<float>(level => _music = level), _music, on ? 1f : 0f, seconds)
            .SetTrans(Tween.TransitionType.Sine).SetEase(on ? Tween.EaseType.Out : Tween.EaseType.In);
        if (!on) _fade.TweenCallback(Callable.From(PauseLayers));
    }

    /// <summary>Plays one of Balatro's sounds (e.g. "paper1") at a pitch and volume, as Balatro would.</summary>
    public void Play(string sound, float pitch = 1f, float volume = 1f)
    {
        AudioStream? stream = Stream(sound);
        if (stream == null) return;
        AudioStreamPlayer player;
        if (_idle.Count > 0)
        {
            player = _idle[^1];
            _idle.RemoveAt(_idle.Count - 1);
        }
        else
        {
            player = new AudioStreamPlayer();
            _holder.AddChild(player);
            AudioStreamPlayer finished = player;
            player.Finished += () => _idle.Add(finished);
        }
        player.Stream = stream;
        player.PitchScale = Math.Max(pitch, 0.05f);
        player.VolumeDb = Mathf.LinearToDb(Math.Max(volume * Gain(Settings?.VolumeSfx ?? 0.5f), 1e-5f));
        player.Play();
    }

    /// <summary>Starts a sound that runs until stopped (a crackling fire), fading in from somewhere into it.</summary>
    public AudioStreamPlayer? StartLoop(string sound, float volume, double fadeIn = 0.25)
    {
        AudioStream? stream = Stream(sound);
        if (stream == null) return null;
        var player = new AudioStreamPlayer { Stream = stream, VolumeDb = -60 };
        _holder.AddChild(player);
        player.Play((float)(stream.GetLength() * GD.Randf() * 0.5));
        float target = Mathf.LinearToDb(Math.Max(volume * Gain(Settings?.VolumeSfx ?? 0.5f), 1e-5f));
        player.CreateTween().TweenProperty(player, "volume_db", target, fadeIn);
        return player;
    }

    /// <summary>Fades out a sound started with <see cref="StartLoop"/> and lets it go.</summary>
    public static void StopLoop(AudioStreamPlayer? player, double fadeOut = 0.6)
    {
        if (player == null || !GodotObject.IsInstanceValid(player)) return;
        Tween t = player.CreateTween();
        t.TweenProperty(player, "volume_db", -60f, fadeOut);
        t.TweenCallback(Callable.From(player.QueueFree));
    }

    private void Update()
    {
        if (!_started) return;
        float delta = (float)_clock.Elapsed.TotalSeconds;
        _clock.Restart();
        float step = Math.Min(1f, delta * LayerFadeRate);
        float music = _music * MusicVolume * Gain(Settings?.VolumeBgm ?? 0.5f);
        for (int i = 0; i < _layers.Length; i++)
        {
            float target = i == (int)_track ? 1f : 0f;
            _layerLevels[i] += (target - _layerLevels[i]) * step;
            _layers[i].VolumeDb = Mathf.LinearToDb(Math.Max(music * _layerLevels[i], 1e-5f));
        }
    }

    // All layers start together so they stay in sync, then pause and resume together.
    private void StartLayers()
    {
        if (!_started)
        {
            _started = true;
            _clock.Restart();
            foreach (AudioStreamPlayer layer in _layers) layer.Play();
            return;
        }
        foreach (AudioStreamPlayer layer in _layers) layer.StreamPaused = false;
    }

    private void PauseLayers()
    {
        if (_musicOn) return;
        foreach (AudioStreamPlayer layer in _layers) layer.StreamPaused = true;
    }

    private static SettingsSave? Settings => SaveManager.Instance?.SettingsSave;

    // StS2 squares its volume sliders into gains (the master slider is applied by the master bus).
    private static float Gain(float slider) => slider * slider;

    private static AudioStream? Stream(string name) => Streams.GetOrAdd(name, n => new Lazy<AudioStream?>(() => Load(n))).Value;

    private static AudioStream? Load(string name)
    {
        string root = Path.GetDirectoryName(BridgeMod.Config.BalatroExe ?? "") ?? "";
        string path = Path.Combine(root, "resources", "sounds", name + ".ogg");
        if (!File.Exists(path))
        {
            BridgeMod.LogError($"Balatro sound missing: {path} (rebuild with tools/build_balatro.py)");
            return null;
        }
        AudioStreamOggVorbis stream = AudioStreamOggVorbis.LoadFromFile(path);
        stream.Loop = name.StartsWith("music", StringComparison.Ordinal);
        return stream;
    }
}
