using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BalatroBridge.Core;

/// <summary>
/// Settings from BalatroBridge.config.json next to the mod DLL. Missing file or fields fall back
/// to defaults. Parsed by hand because reflection-based JSON serialization isn't guaranteed in the host.
/// </summary>
public sealed class BridgeConfig
{
    public const string FileName = "BalatroBridge.config.json";
    public const int DefaultPort = 47291;

    /// <summary>Localhost port the bridge listens on.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>Path to the bridged Balatro.exe produced by tools/build_balatro.py.</summary>
    public string? BalatroExe { get; init; }

    /// <summary>Launch Balatro when the mod loads, so the first hand is dealt by the time it's needed.</summary>
    public bool AutoLaunch { get; init; } = true;

    /// <summary>Run Balatro as an invisible engine (StS2 draws the table). Turn off to watch Balatro while debugging.</summary>
    public bool Hidden { get; init; } = true;

    public static BridgeConfig Load(string path, Action<string> log)
    {
        if (!File.Exists(path))
        {
            log($"no {FileName} found at {path}; using defaults");
            return new BridgeConfig();
        }
        try
        {
            JsonObject obj = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException("not a JSON object");
            return new BridgeConfig
            {
                Port = obj["port"]?.GetValue<int>() ?? DefaultPort,
                BalatroExe = obj["balatro_exe"]?.GetValue<string>(),
                AutoLaunch = obj["auto_launch"]?.GetValue<bool>() ?? true,
                Hidden = obj["hidden"]?.GetValue<bool>() ?? true,
            };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or IOException)
        {
            log($"could not read {path} ({e.Message}); using defaults");
            return new BridgeConfig();
        }
    }
}
