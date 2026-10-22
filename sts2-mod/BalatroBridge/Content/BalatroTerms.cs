using System.Text.RegularExpressions;

namespace BalatroBridge.Content;

/// <summary>
/// Balatro's words in Balatro's own colours, the shades its text uses (globals.lua's G.C):
/// Mult red, Chips blue, money gold, discards red, poker hands and other highlighted words
/// (jokers, blinds, the ante, vouchers) orange, tarots, planets and spectrals in their set
/// colours, and the four suits. Used on what Jimbo says.
/// </summary>
internal static class BalatroTerms
{
    public const string Mult = "FE5F55";
    public const string Chips = "009DFF";
    public const string Money = "F3B958";
    public const string Attention = "FF9A00";
    public const string Tarot = "A782D1";
    public const string Planet = "13AFCE";
    public const string Spectral = "4584FA";
    public const string Hearts = "F03464";
    public const string Diamonds = "F06B3F";
    public const string Spades = "403995";
    public const string Clubs = "235955";

    // Longer terms first, so "Straight Flush" isn't coloured as "Straight" and "Flush".
    private static readonly (string Pattern, string Colour)[] Terms =
    [
        (@"x ?mult", Mult),
        (@"mult", Mult),
        (@"chips?", Chips),
        (@"\$\d+(?:[.,]\d+)?|money|dollars?", Money),
        (@"discards?", Mult),
        (@"high card|two pair|three of a kind|four of a kind|five of a kind|straight flush|royal flush|flush house|flush five|full house|straight|flush|pair", Attention),
        (@"jokers?|boss blinds?|small blinds?|big blinds?|blinds?|antes?|vouchers?", Attention),
        (@"tarots?", Tarot),
        (@"planets?", Planet),
        (@"spectrals?", Spectral),
        (@"hearts?", Hearts),
        (@"diamonds?", Diamonds),
        (@"spades?", Spades),
        (@"clubs?", Clubs),
    ];

    private static readonly Regex Words = new(
        string.Join("|", System.Array.ConvertAll(Terms, t => $@"(?<!\w)(?:{t.Pattern})(?!\w)")),
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Tags = new(@"(\[[^\]]*\])", RegexOptions.Compiled);
    private static readonly Regex Quotes = new("(\"[^\"]*\")", RegexOptions.Compiled);

    /// <summary>The text with each Balatro term in its colour; markup already in it is left alone.</summary>
    public static string Colour(string text)
    {
        string[] parts = Tags.Split(text);
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith('[')) continue;
            parts[i] = Words.Replace(parts[i], m => $"[color=#{ColourOf(m.Value)}]{m.Value}[/color]");
        }
        return string.Concat(parts);
    }

    /// <summary>Colours only what's said in quotes (an event's narration around it stays as it is).</summary>
    public static string ColourQuotes(string text) => Quotes.Replace(text, m => Colour(m.Value));

    private static string ColourOf(string word)
    {
        foreach ((string pattern, string colour) in Terms)
        {
            if (Regex.IsMatch(word, $"^(?:{pattern})$", RegexOptions.IgnoreCase)) return colour;
        }
        return Attention;
    }
}
