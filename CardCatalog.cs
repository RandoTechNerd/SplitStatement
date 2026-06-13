namespace MoneySplit;

/// <summary>
/// What the app "knows" out of the box about common U.S. credit cards: a display name,
/// the skin (CSS class) to paint it, a one-line reward summary, and keywords used to
/// auto-detect the card from its name / statement filename. New cards are matched against
/// this so they arrive looking right with sensible rewards; anything unmatched is left
/// "unknown" and flagged for the user to classify.
/// </summary>
public record CardType(string Key, string Name, string Skin, string Rewards, string[] Keywords);

public static class CardCatalog
{
    public static readonly CardType[] All =
    {
        new("prime-visa", "Amazon Prime Visa", "card-chase-amz",
            "5% Amazon & Whole Foods, 2% dining/gas/drugstore, 1% else",
            new[] { "prime", "amazon", "amzn", "amz" }),
        new("chase-sapphire", "Chase Sapphire", "card-chase-sap",
            "3x dining & travel, 1x else", new[] { "sapphire" }),
        new("chase-freedom", "Chase Freedom", "card-chase-sap",
            "5% rotating quarterly, 3% dining & drugstore, 1% else", new[] { "freedom" }),
        new("chase-generic", "Chase (other)", "card-chase-sap",
            "varies", new[] { "chase" }),
        new("amex-gold", "Amex Gold", "card-gold",
            "4x dining & U.S. groceries, 3x flights", new[] { "amex gold", "gold card" }),
        new("amex-platinum", "Amex Platinum", "card-gold",
            "5x flights & prepaid hotels", new[] { "platinum" }),
        new("amex-bcp", "Amex Blue Cash Preferred", "card-amex-pref",
            "6% U.S. groceries & streaming, 3% gas & transit, 1% else",
            new[] { "blue cash preferred", "blue cash pref", "bcp" }),
        new("amex-bce", "Amex Blue Cash Everyday", "card-amex-blue",
            "3% U.S. groceries, online retail & gas, 1% else", new[] { "blue cash everyday", "bce" }),
        new("amex-generic", "Amex (other)", "card-amex-blue",
            "varies", new[] { "amex", "american express" }),
        new("cap1-venture", "Capital One Venture", "card-cap1-ven",
            "2x miles on everything", new[] { "venture" }),
        new("cap1-savor", "Capital One Savor", "card-cap1-ven",
            "4% dining & entertainment, 3% groceries", new[] { "savor" }),
        new("cap1-quicksilver", "Capital One Quicksilver", "card-cap1-ven",
            "1.5% flat cash back", new[] { "quicksilver" }),
        new("cap1-generic", "Capital One (other)", "card-cap1-ven",
            "varies", new[] { "capital one", "cap 1", "cap1", "capone" }),
        new("bilt", "BILT Mastercard", "card-bilt",
            "rent with no fee, 3x dining, 2x travel, 1x else", new[] { "bilt" }),
        new("citi-double", "Citi Double Cash", "card-citi",
            "2% everywhere (1% buy + 1% pay)", new[] { "double cash", "citi double" }),
        new("citi-custom", "Citi Custom Cash", "card-citi",
            "5% on your top category each cycle", new[] { "custom cash" }),
        new("citi-generic", "Citi (other)", "card-citi",
            "varies", new[] { "citi" }),
        new("discover-it", "Discover it", "card-discover",
            "5% rotating quarterly, 1% else", new[] { "discover" }),
        new("wells-active", "Wells Fargo Active Cash", "card-wf",
            "2% flat cash back", new[] { "active cash" }),
        new("wells-autograph", "Wells Fargo Autograph", "card-wf",
            "3x dining, travel, gas, transit & streaming", new[] { "autograph" }),
        new("wells-generic", "Wells Fargo (other)", "card-wf",
            "varies", new[] { "wells fargo", "wellsfargo", "wells" }),
        new("aaa-travel", "AAA Travel Advantage", "card-aaa",
            "5% gas/EV (cap), 3% grocery/travel, 1% else", new[] { "aaa", "travel advantage" }),
        new("alaska", "Alaska Airlines Visa", "card-chase-sap",
            "3x Alaska, 2x gas/dining/local transit, 1x else", new[] { "alaska", "ak air", "ak-air" }),
        new("other", "Other / Custom", "card-chase-sap", "—", new string[0]),
    };

    public static CardType ByKey(string? key) =>
        All.FirstOrDefault(c => c.Key == key) ?? All.First(c => c.Key == "other");

    /// <summary>Reward chips to auto-apply when a card type is chosen (colors group categories:
    /// green=grocery, blue=dining, purple=travel, orange=gas, teal=streaming, pink=rotating/special,
    /// grey=flat/rent). Users can add/remove afterward.</summary>
    public static (string label, string color)[] TagsFor(string? key) => key switch
    {
        "prime-visa" => new[] { ("5% Amazon", "green"), ("5% Whole Foods", "green"), ("2% Dining", "blue"), ("2% Gas", "orange") },
        "chase-sapphire" => new[] { ("3x Travel", "purple"), ("3x Dining", "blue") },
        "chase-freedom" => new[] { ("5% Rotating", "pink"), ("3% Dining", "blue"), ("3% Drugstore", "grey") },
        "amex-gold" => new[] { ("4x Dining", "blue"), ("4x Groceries", "green"), ("3x Flights", "purple") },
        "amex-platinum" => new[] { ("5x Flights", "purple"), ("5x Hotels", "purple") },
        "amex-bcp" => new[] { ("6% Groceries", "green"), ("6% Streaming", "teal"), ("3% Gas", "orange"), ("3% Transit", "purple") },
        "amex-bce" => new[] { ("3% Groceries", "green"), ("3% Online", "pink"), ("3% Gas", "orange") },
        "cap1-venture" => new[] { ("2x Everything", "grey") },
        "cap1-savor" => new[] { ("4% Dining", "blue"), ("4% Entertainment", "pink"), ("3% Groceries", "green") },
        "cap1-quicksilver" => new[] { ("1.5% Flat", "grey") },
        "bilt" => new[] { ("Rent (no fee)", "grey"), ("3x Dining", "blue"), ("2x Travel", "purple") },
        "citi-double" => new[] { ("2% Everywhere", "grey") },
        "citi-custom" => new[] { ("5% Top Category", "pink") },
        "discover-it" => new[] { ("5% Rotating", "pink") },
        "wells-active" => new[] { ("2% Flat", "grey") },
        "wells-autograph" => new[] { ("3x Dining", "blue"), ("3x Travel", "purple"), ("3x Gas", "orange"), ("3x Streaming", "teal") },
        "aaa-travel" => new[] { ("5% Gas", "orange"), ("3% Grocery", "green"), ("3% Travel", "purple") },
        "alaska" => new[] { ("3x Alaska", "purple"), ("2x Gas & Dining", "blue") },
        _ => System.Array.Empty<(string, string)>(),
    };

    /// <summary>Merge this type's reward chips into a card note (skips labels already present).</summary>
    public static string ApplyTags(string? note, string? key)
    {
        string n = note ?? "";
        foreach (var (label, color) in TagsFor(key))
            if (!System.Text.RegularExpressions.Regex.IsMatch(n,
                    $@"\(\s*{System.Text.RegularExpressions.Regex.Escape(label)}\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                n = (n + $" ({label}:{color})").Trim();
        return n;
    }

    /// <summary>Best-guess type from a card name and/or statement filename. The NAME is the
    /// stronger signal (the filename is just the issuer — a Chase-issued Prime Visa shouldn't
    /// match "chase"), so we try the name alone first, then fall back to the filename.</summary>
    public static CardType? Detect(string? cardName, string? fileName = null)
        => MatchIn(cardName) ?? MatchIn(fileName);

    private static CardType? MatchIn(string? text)
    {
        string hay = (text ?? "").ToLowerInvariant();
        if (hay.Trim().Length == 0) return null;
        CardType? best = null; int bestLen = 0;
        foreach (var c in All)
            foreach (var kw in c.Keywords)
                if (hay.Contains(kw) && kw.Length > bestLen)   // longest keyword wins (most specific)
                { best = c; bestLen = kw.Length; }
        return best;
    }
}
