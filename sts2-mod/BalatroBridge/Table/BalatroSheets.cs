using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Godot;

namespace BalatroBridge.Table;

/// <summary>
/// Balatro's sprite sheets, decoded from the bridged build's folder (tools/build_balatro.py
/// extracts them there). Decoding a big PNG on the game thread drops frames, so the sheets every
/// table uses are decoded on a worker thread at startup. Any other sheet, or one still being
/// decoded, is waited for on first use.
/// </summary>
internal static class BalatroSheets
{
    public const string Jokers = "resources/textures/2x/Jokers.png";

    // Balatro's default art at 2x: the deck, enhancements, jokers, blind chips and the Slay the
    // Spire Clubs collab the hidden engine uses.
    private static readonly string[] Common =
    [
        "resources/textures/2x/8BitDeck.png",
        "resources/textures/2x/Enhancers.png",
        Jokers,
        "resources/textures/2x/BlindChips.png",
        "resources/textures/2x/collabs/collab_STS_1.png",
    ];

    private static readonly ConcurrentDictionary<string, Lazy<Image?>> Images = new();

    /// <summary>Decodes the common sheets in the background, then runs <paramref name="then"/> there too.</summary>
    public static void Preload(Action? then = null) => Task.Run(() =>
    {
        try
        {
            foreach (string sheet in Common) Get(sheet);
            then?.Invoke();
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"preloading Balatro art failed: {e.Message}");
        }
    });

    /// <summary>A decoded sheet (path relative to the Balatro folder), or null if it's missing. Don't modify it: it's shared.</summary>
    public static Image? Get(string relativePath) => Images.GetOrAdd(relativePath, p => new Lazy<Image?>(() => Load(p))).Value;

    private static Image? Load(string relativePath)
    {
        string root = Path.GetDirectoryName(BridgeMod.Config.BalatroExe ?? "") ?? "";
        string path = Path.Combine(root, relativePath).Replace('\\', '/');
        Image? image = File.Exists(path) ? Image.LoadFromFile(path) : null;
        if (image == null) BridgeMod.LogError($"Balatro texture missing: {path} (rebuild with tools/build_balatro.py)");
        return image;
    }
}
