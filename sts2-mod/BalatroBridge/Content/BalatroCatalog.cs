using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using BalatroBridge.Core;

namespace BalatroBridge.Content;

/// <summary>
/// Every Balatro item's name and text (jokers, vouchers, decks, consumables, stakes, tags and
/// blinds), as the running Balatro words them: its own localization, with each item's starting
/// values filled in. StS2 shows these for relics and potions before a run has live values for
/// them. The answer is kept in StS2's user folder (not the mod's: StS2 reads every JSON file there
/// as a mod manifest), so a launch has the text from the start; Balatro is asked again each time
/// it connects, so changes to Balatro or to how the bridge writes its text come through.
/// </summary>
internal static class BalatroCatalog
{
    public const string FileName = "balatro_catalog.json";

    private static Dictionary<string, Entry> _entries = new();
    private static string? _version;

    public sealed record Entry(string Set, string Name, string? Text);

    /// <summary>Raised (on the main thread) when new text arrives from Balatro.</summary>
    public static event Action? Changed;

    public static bool IsLoaded => _entries.Count > 0;

    public static Entry? Get(string? key) => key != null && _entries.TryGetValue(key, out Entry? e) ? e : null;

    public static string? Name(string? key) => Get(key)?.Name;

    public static string? Text(string? key) => Get(key)?.Text;

    private static string FilePath => Path.Combine(Godot.OS.GetUserDataDir(), FileName);

    /// <summary>Reads the kept catalog, if there is one.</summary>
    public static void LoadSaved()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            if (JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject data) Apply(data, save: false);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
            BridgeMod.LogError($"couldn't read {FileName}: {e.Message}");
        }
    }

    /// <summary>Asks Balatro for its text (the kept catalog stands in until it answers).</summary>
    public static async Task Refresh(BalatroSession session)
    {
        try
        {
            JsonObject data = await session.Catalog();
            Apply(data, save: true);
            BridgeMod.LogInfo($"catalog: {_entries.Count} Balatro items from Balatro {_version}");
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"couldn't get Balatro's catalog: {e.Message}");
        }
    }

    private static void Apply(JsonObject data, bool save)
    {
        if (data["items"] is not JsonObject items) return;
        var entries = new Dictionary<string, Entry>();
        foreach ((string key, JsonNode? node) in items)
        {
            if (node is not JsonObject item) continue;
            entries[key] = new Entry(BalatroState.Str(item["set"]) ?? "", BalatroState.Str(item["name"]) ?? key, BalatroState.Str(item["text"]));
        }
        if (entries.Count == 0) return;
        _entries = entries;
        _version = BalatroState.Str(data["version"]);
        if (save)
        {
            try
            {
                File.WriteAllText(FilePath, data.ToJsonString());
            }
            catch (IOException e)
            {
                BridgeMod.LogError($"couldn't keep the catalog: {e.Message}");
            }
        }
        Changed?.Invoke();
    }
}
