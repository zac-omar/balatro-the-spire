using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Saves;

namespace BalatroBridge.Table;

/// <summary>
/// Crossfades StS2's music with Balatro's (<see cref="BalatroAudio"/>) while the table is open:
/// StS2's music and ambience ease out as Balatro's soundtrack swells in, and the reverse when the
/// table closes. Coming back, Balatro's music leads and StS2's follows a beat later, so the
/// fight's music returns under the tail of the card table's. StS2's sound effects stay on.
/// </summary>
internal static class TableMusic
{
    private const double Sts2FadeOut = 1.0;
    private const double BalatroFadeIn = 1.2;
    private const double BalatroFadeOut = 1.4;
    private const double Sts2FadeIn = 1.6;
    private const double Sts2ReturnDelay = 0.35;

    private static Tween? _sts2Fade;
    private static float _sts2Level = 1f; // where StS2's music is between silence (0) and the settings (1)

    public static void Start() => Swap(balatro: true);

    public static void Stop() => Swap(balatro: false);

    // Music is a nicety: a failure here must never get in the way of the hand.
    private static void Swap(bool balatro)
    {
        try
        {
            BalatroAudio.Instance.FadeMusic(on: balatro, balatro ? BalatroFadeIn : BalatroFadeOut);
            if (balatro) FadeSts2(to: 0f, Sts2FadeOut, delay: 0);
            else FadeSts2(to: 1f, Sts2FadeIn, Sts2ReturnDelay);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"music swap failed: {e.Message}");
        }
    }

    /// <summary>
    /// Fades StS2's music and ambience from wherever they are to silence (0) or the player's
    /// settings (1). StS2 squares these volumes, so a sine ease sounds even rather than sudden.
    /// </summary>
    private static void FadeSts2(float to, double seconds, double delay)
    {
        NAudioManager? audio = NGame.Instance?.AudioManager;
        if (audio == null) return;
        SettingsSave settings = SaveManager.Instance.SettingsSave;
        _sts2Fade?.Kill();
        Tween tween = ((SceneTree)Engine.GetMainLoop()).CreateTween();
        tween.TweenMethod(Callable.From<float>(level =>
        {
            _sts2Level = level;
            audio.SetBgmVol(settings.VolumeBgm * level);
            audio.SetAmbienceVol(settings.VolumeAmbience * level);
        }), _sts2Level, to, seconds).SetDelay(delay).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        _sts2Fade = tween;
    }
}
