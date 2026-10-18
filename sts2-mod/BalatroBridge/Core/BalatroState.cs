using System.Globalization;
using System.Text.Json.Nodes;

namespace BalatroBridge.Core;

/// <summary>Typed view of Balatro's "state" message. <see cref="Raw"/> keeps every field.</summary>
public sealed record BalatroState(string? Stage, string? State, BalatroRun? Run, JsonObject Raw)
{
    public bool InRun => Run != null;

    public static BalatroState FromJson(JsonObject data)
    {
        BalatroRun? run = data["run"] is JsonObject r ? BalatroRun.FromJson(r) : null;
        return new BalatroState(Str(data["stage"]), Str(data["state"]), run, data);
    }

    public override string ToString() =>
        Run == null ? $"{Stage}/{State}" : $"{Stage}/{State} {Run}";

    internal static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>Reads a number, accepting the "inf"/"-inf"/"nan" strings Balatro uses for non-finite values.</summary>
    internal static double? Num(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return d;
        return Str(node) switch
        {
            "inf" => double.PositiveInfinity,
            "-inf" => double.NegativeInfinity,
            "nan" => double.NaN,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => null,
        };
    }

    internal static int? Int(JsonNode? node) => Num(node) is double d && double.IsFinite(d) ? (int)d : null;

    internal static bool Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) && b;
}

public sealed record BalatroRun(
    string? Seed,
    int? Stake,
    string? Deck,
    int? Ante,
    int? Round,
    double? Dollars,
    double? Chips,
    int? HandsLeft,
    int? DiscardsLeft,
    bool Won,
    BalatroBlind? Blind)
{
    public static BalatroRun FromJson(JsonObject r) => new(
        BalatroState.Str(r["seed"]),
        BalatroState.Int(r["stake"]),
        BalatroState.Str(r["deck"]),
        BalatroState.Int(r["ante"]),
        BalatroState.Int(r["round"]),
        BalatroState.Num(r["dollars"]),
        BalatroState.Num(r["chips"]),
        BalatroState.Int(r["hands_left"]),
        BalatroState.Int(r["discards_left"]),
        BalatroState.Bool(r["won"]),
        r["blind"] is JsonObject b ? BalatroBlind.FromJson(b) : null);

    public override string ToString() =>
        $"ante {Ante} round {Round} ${Dollars} chips {Chips} hands {HandsLeft} discards {DiscardsLeft}" +
        (Blind != null ? $" vs {Blind}" : "");
}

public sealed record BalatroBlind(string Name, double? Chips, double? Dollars, bool Boss)
{
    public static BalatroBlind FromJson(JsonObject b) => new(
        BalatroState.Str(b["name"]) ?? "?",
        BalatroState.Num(b["chips"]),
        BalatroState.Num(b["dollars"]),
        BalatroState.Bool(b["boss"]));

    public override string ToString() => $"{Name} ({Chips} chips)";
}
