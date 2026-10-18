using System;
using System.Collections.Generic;
using System.Linq;
using BalatroBridge.Table;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Audio;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Jimbo's voice and sounds are Balatro's. StS2 plays a character's sounds as FMOD events named
/// after it (event:/sfx/characters/jimbo/jimbo_attack, ...), which don't exist for Jimbo; this
/// catches them on their way to FMOD and plays Balatro's sounds instead: his babble when picked
/// or when he talks, a card whoosh for attacks, a tarot shimmer for spells, crumpling paper when
/// he falls.
/// </summary>
[HarmonyPatch(typeof(NAudioManager), nameof(NAudioManager.PlayOneShot), [typeof(string), typeof(Dictionary<string, float>), typeof(float)])]
internal static class JimboSounds
{
    private const string EventPrefix = "event:/sfx/characters/jimbo/";
    public const string SelectEvent = EventPrefix + "jimbo_select";
    public const string TransitionEvent = "event:/sfx/ui/wipe_jimbo";

    /// <summary>The Balatro sounds Jimbo uses, loaded ahead of time with the table's.</summary>
    internal static readonly string[] Sounds =
    [
        "voice1", "voice2", "voice3", "voice4", "voice5", "voice6", "voice7", "voice8", "voice9", "voice10", "voice11",
        "card3", "slice1", "tarot1", "tarot2", "crumple1", "crumpleLong1", "whoosh_long", "gong", "win", "timpani",
        "explosion_release1", "ambientFire2",
    ];

    private static bool Prefix(string path, float volume)
    {
        if (!path.StartsWith(EventPrefix) && path != TransitionEvent) return true;
        BalatroAudio audio = BalatroAudio.Instance;
        switch (path[(path.LastIndexOf('/') + 1)..])
        {
            case "jimbo_select":
                Babble(6);
                audio.Play("cardFan2", 1f, 0.7f * volume);
                break;
            case "jimbo_attack":
                audio.Play("card3", 0.9f + 0.2f * GD.Randf(), 0.8f * volume);
                audio.Play("whoosh1", 1.1f, 0.4f * volume);
                break;
            case "jimbo_cast":
                audio.Play("tarot1", 1f, 0.8f * volume);
                break;
            case "jimbo_die":
                audio.Play("crumpleLong1", 0.8f, volume);
                Babble(4, pitch: 0.7f);
                break;
            case "wipe_jimbo":
                audio.Play("whoosh_long", 1f, 0.7f * volume);
                audio.Play("cardFan2", 0.9f, 0.5f * volume);
                break;
        }
        return false;
    }

    // Balatro's Jimbo says a syllable every 0.13s, never the same voice clip twice in a row.
    private const double Syllable = 0.13;
    private static int _lastVoice;
    private static ulong _talk;

    /// <summary>
    /// Jimbo talking, as Balatro voices him: a string of short voice clips, one per syllable,
    /// each a random clip at a slightly random pitch. <paramref name="onSyllable"/> runs with each
    /// (to bounce whoever is speaking). A new line cuts off the last one.
    /// </summary>
    public static void Babble(int syllables = 3, float pitch = 1f, Action? onSyllable = null, double delay = 0)
    {
        ulong talk = ++_talk;
        var tree = (SceneTree)Engine.GetMainLoop();
        for (int i = 0; i < syllables; i++)
        {
            SceneTreeTimer timer = tree.CreateTimer(delay + Syllable * i / TableFx.Speed);
            timer.Timeout += () =>
            {
                if (talk != _talk) return;
                int voice = _lastVoice == 0 ? GD.RandRange(1, 11) : GD.RandRange(1, 10);
                if (_lastVoice != 0 && voice >= _lastVoice) voice++; // 1-11, skipping the last one
                _lastVoice = voice;
                BalatroAudio.Instance.Play("voice" + voice, pitch * (1f + 0.2f * GD.Randf()), 0.5f);
                onSyllable?.Invoke();
            };
        }
    }

    /// <summary>Jimbo (or the Dealer) says <paramref name="line"/>: as many syllables as it takes to say it.</summary>
    public static void Say(string line, Action? onSyllable = null, double delay = 0)
    {
        if (!string.IsNullOrWhiteSpace(line)) Babble(SyllablesFor(line), onSyllable: onSyllable, delay: delay);
    }

    /// <summary>About one syllable per three letters of what's said, markup aside.</summary>
    public static int SyllablesFor(string line)
    {
        int letters = System.Text.RegularExpressions.Regex.Replace(line, @"\[[^\]]*\]", "").Count(char.IsLetterOrDigit);
        return Math.Clamp((letters + 2) / 3, 3, 40);
    }

    /// <summary>The quoted (spoken) parts of a passage of narration; empty if nobody speaks.</summary>
    public static string Spoken(string narration) =>
        string.Join(" ", System.Text.RegularExpressions.Regex.Matches(narration, "\"([^\"]*)\"").Select(m => m.Groups[1].Value));
}
