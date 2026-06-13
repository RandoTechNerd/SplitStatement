using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Dapper;

namespace MoneySplit;

public record ParsedRow(DateTime? TxnDate, DateTime? PostDate, string Description,
                        string? Category, double Amount, bool LooksLikePayment,
                        string Raw = "", string CardNo = "", bool Pending = false);

public record PaymentSeen(string? Date, double Amount, string Description, string? Who = null);

public record ImportResult(int Added, int Duplicates, int NeedsReview, int SkippedPayments,
                           List<string> Warnings, List<PaymentSeen> Payments,
                           int MatchedPending = 0);

/// <summary>
/// Turns whatever the banks export (Chase / Amex / Capital One / generic CSV, or an Excel
/// sheet of the same) into transactions: detects the column layout from the headers,
/// normalizes signs so positive = charge, skips card-payment rows, dedupes via a content
/// hash (so overlapping date-range exports are safe), and buckets each charge using the
/// learned merchant rules — flagging anything it isn't confident about.
/// </summary>
public static class Importer
{
    // ---------- public entry ----------
    public static ImportResult Import(long cardId, string fileName, Stream content)
    {
        var rows = fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".xls", StringComparison.OrdinalIgnoreCase)
            ? ReadExcel(content)
            : ReadCsv(content);

        var warnings = new List<string>();
        var parsed = ParseRows(rows, warnings);
        return Insert(cardId, fileName, parsed, warnings);
    }

    // ---------- file readers (everything becomes a list of string rows) ----------
    /// <summary>
    /// Full RFC-style CSV reader: quoted fields may contain commas, escaped quotes, AND
    /// newlines (AAA/Bread Financial exports embed line breaks inside the Location column).
    /// A line-based reader chops those rows in half, so we walk the whole text instead.
    /// </summary>
    private static List<string[]> ReadCsv(Stream s)
    {
        using var reader = new StreamReader(s);
        string text = reader.ReadToEnd();

        var rows = new List<string[]>();
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        void EndCell() { cells.Add(sb.ToString()); sb.Clear(); }
        void EndRow()
        {
            EndCell();
            if (cells.Any(c => c.Trim().Length > 0)) rows.Add(cells.ToArray());
            cells.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else sb.Append(c == '\n' || c == '\r' ? ' ' : c);   // flatten embedded newlines
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') EndCell();
            else if (c == '\n') EndRow();
            else if (c != '\r') sb.Append(c);
        }
        if (sb.Length > 0 || cells.Count > 0) EndRow();
        return rows;
    }

    private static List<string[]> ReadExcel(Stream s)
    {
        using var wb = new XLWorkbook(s);
        var ws = wb.Worksheets.First();
        var rows = new List<string[]>();
        foreach (var row in ws.RangeUsed()?.Rows() ?? Enumerable.Empty<IXLRangeRow>())
            rows.Add(row.Cells().Select(c => c.GetFormattedString()).ToArray());
        return rows;
    }

    // ---------- layout detection + parsing ----------
    private static List<ParsedRow> ParseRows(List<string[]> rows, List<string> warnings)
    {
        if (rows.Count == 0) return new();

        // Find the header row: the first row mentioning a date + description/amount-ish column.
        int headerIdx = -1;
        for (int i = 0; i < Math.Min(rows.Count, 8); i++)
        {
            var low = rows[i].Select(c => c.Trim().ToLowerInvariant()).ToArray();
            bool hasDate = low.Any(c => c.Contains("date"));
            bool hasDesc = low.Any(c => c.Contains("description") || c.Contains("merchant") || c.Contains("payee"));
            if (hasDate && hasDesc) { headerIdx = i; break; }
        }
        if (headerIdx < 0)
        {
            warnings.Add("No header row found — assuming columns: Date, Description, Amount.");
            return rows.Select(r => ParseGeneric(r)).Where(p => p != null).Select(p => p!).ToList();
        }

        var headers = rows[headerIdx].Select(c => c.Trim().ToLowerInvariant()).ToArray();
        int Col(params string[] names) =>
            Array.FindIndex(headers, h => names.Any(n => h == n || h.Contains(n)));

        int cTxnDate  = Col("transaction date", "trans. date", "trans date", "date");
        int cPostDate = Col("post date", "posted date", "posting date");
        int cDesc     = Col("description", "merchant", "payee");
        int cCategory = Col("category");
        int cType     = Col("type");
        int cAmount   = Col("amount");
        int cDebit    = Col("debit");
        int cCredit   = Col("credit");
        int cCardNo   = Col("card no", "card last", "card number", "account #", "account number");
        int cStatus   = Col("status");

        var parsed = new List<ParsedRow>();
        for (int i = headerIdx + 1; i < rows.Count; i++)
        {
            var r = rows[i];
            string Cell(int idx) => idx >= 0 && idx < r.Length ? r[idx].Trim() : "";

            string desc = Cell(cDesc);
            if (desc.Length == 0) continue;

            DateTime? txnDate = ParseDate(Cell(cTxnDate));
            DateTime? postDate = ParseDate(Cell(cPostDate));
            if (txnDate == null && postDate == null) continue;

            double amount;
            if (cDebit >= 0 || cCredit >= 0)
            {
                // Capital One style: Debit = charge, Credit = payment/refund.
                double debit = ParseMoney(Cell(cDebit)) ?? 0;
                double credit = ParseMoney(Cell(cCredit)) ?? 0;
                amount = debit != 0 ? debit : -credit;
            }
            else
            {
                var a = ParseMoney(Cell(cAmount));
                if (a == null) continue;
                amount = a.Value;
            }

            string type = Cell(cType);
            bool isPayment = LooksLikePayment(desc, type) ||
                             Cell(cCategory).Equals("payment", StringComparison.OrdinalIgnoreCase);
            bool pending = Cell(cStatus).Equals("pending", StringComparison.OrdinalIgnoreCase);
            parsed.Add(new ParsedRow(txnDate, postDate, desc, NullIfEmpty(Cell(cCategory)), amount,
                                     isPayment, string.Join(",", r), Cell(cCardNo), pending));
        }

        // Sign normalization: bank exports disagree on whether charges are + or -.
        // Charges always outnumber refunds, so if most non-payment amounts are negative, flip.
        var charges = parsed.Where(p => !p.LooksLikePayment).ToList();
        if (charges.Count > 0 && charges.Count(p => p.Amount < 0) > charges.Count / 2)
            parsed = parsed.Select(p => p with { Amount = -p.Amount }).ToList();

        return parsed;
    }

    private static ParsedRow? ParseGeneric(string[] r)
    {
        if (r.Length < 3) return null;
        var date = ParseDate(r[0]);
        var amount = ParseMoney(r[^1]);
        if (date == null || amount == null) return null;
        string desc = string.Join(" ", r[1..^1]).Trim();
        return new ParsedRow(date, null, desc, null, amount.Value, LooksLikePayment(desc, ""),
                             string.Join(",", r));
    }

    private static bool LooksLikePayment(string desc, string type)
    {
        // Rent flowing through the card (Bilt's in-and-out pair) should stay VISIBLE,
        // and a "late payment fee" is a charge — neither is a card-balance payment.
        if (Regex.IsMatch(desc, @"rent|housing|fee", RegexOptions.IgnoreCase)) return false;
        return Regex.IsMatch(desc, @"payment|autopay|thank you|\bpym?t\b|epay|directpay|ach deposit",
                             RegexOptions.IgnoreCase) ||
               type.Equals("payment", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime? ParseDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string[] formats = { "M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "M/d/yy", "MM/dd/yy", "dd MMM yyyy" };
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : null;
    }

    private static double? ParseMoney(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Replace("$", "").Replace(",", "").Trim();
        if (s.StartsWith('(') && s.EndsWith(')')) s = "-" + s[1..^1];
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    // ---------- merchant normalization + rules ----------
    /// <summary>"AMAZON MKTPL*BV8V689I2" → "AMAZON MKTPL"; "Whole Foods INT 10275" → "WHOLE FOODS INT".</summary>
    public static string NormalizeMerchant(string desc)
    {
        string s = desc.ToUpperInvariant();
        s = Regex.Replace(s, @"\*\S*", " ");          // order-id suffixes: AMZN*1A2B3C
        s = Regex.Replace(s, @"[^A-Z]+", " ");        // digits, punctuation, phone numbers
        s = Regex.Replace(s, @"\b(APLPAY|TST|SQ|SP|PAYPAL)\b", " "); // wallet/processor prefixes
        s = Regex.Replace(s, @"\s+", " ").Trim();
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(3));
    }

    /// <summary>
    /// Recurring-charge matcher first: if this exact amount has shown up before on this card
    /// from the same merchant family and always landed in one bucket, reuse that bucket AND
    /// its note (the $11.04 "dog treat bag" effect). Falls back to merchant rules.
    /// </summary>
    public static (string Bucket, bool NeedsReview, string? Note) PredictSmart(
        Microsoft.Data.Sqlite.SqliteConnection con, long cardId, string merchant,
        double amount, string cardDefault)
    {
        if (merchant.Length > 0 && Math.Abs(amount) > 0.004)
        {
            string pfx = merchant.Split(' ')[0] + "%";
            var prior = con.Query<(string bucket, string? note)>("""
                SELECT bucket, note FROM txns
                WHERE card_id = @cardId AND ABS(amount - @amount) < 0.005
                  AND bucket IN ('Shared','Mel','Aryn') AND UPPER(description) LIKE @pfx
                ORDER BY id DESC LIMIT 10
                """, new { cardId, amount, pfx }).ToList();
            if (prior.Count > 0 && prior.All(p => p.bucket == prior[0].bucket))
            {
                // Seen 2+ times at this exact amount = a subscription; label it and stop asking.
                string? note = prior.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.note)).note
                               ?? (prior.Count >= 2 ? "🔁 recurring" : null);
                return (prior[0].bucket, prior.Count < 2, note);
            }
        }
        var (bucket, review) = Predict(con, merchant, cardDefault);
        return (bucket, review, null);
    }

    public static (string Bucket, bool NeedsReview) Predict(
        Microsoft.Data.Sqlite.SqliteConnection con, string merchant, string cardDefault)
    {
        var rule = con.QueryFirstOrDefault<(int shared_n, int mel_n, int aryn_n)?>(
            "SELECT shared_n, mel_n, aryn_n FROM rules WHERE merchant = @m", new { m = merchant });
        if (rule == null) return (cardDefault, true);

        var (sn, mn, an) = rule.Value;
        int total = sn + mn + an;
        if (total == 0) return (cardDefault, true);

        var best = new[] { ("Shared", sn), ("Mel", mn), ("Aryn", an) }.MaxBy(t => t.Item2);
        double confidence = (double)best.Item2 / total;
        // Confident only with history AND a dominant bucket (Amazon-style mixed merchants stay flagged).
        return (best.Item1, total < 2 || confidence < 0.8);
    }

    public static void LearnRule(Microsoft.Data.Sqlite.SqliteConnection con, string merchant, string bucket)
    {
        if (merchant.Length == 0 || bucket == "Skip") return;
        string col = bucket switch { "Mel" => "mel_n", "Aryn" => "aryn_n", _ => "shared_n" };
        con.Execute($"""
            INSERT INTO rules (merchant, {col}, updated_at) VALUES (@m, 1, datetime('now'))
            ON CONFLICT(merchant) DO UPDATE SET {col} = {col} + 1, updated_at = datetime('now')
            """, new { m = merchant });
    }

    // ---------- insertion + dedupe ----------
    public static string Hash(long cardId, DateTime? txnDate, string desc, double amount)
    {
        string key = $"{cardId}|{txnDate:yyyy-MM-dd}|{Regex.Replace(desc.ToUpperInvariant(), @"\s+", " ").Trim()}|{amount:0.00}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
    }

    /// <summary>Parse owner-tagged numbers ("Aryn:11009 Mel:11017") → digits → person.
    /// Untagged numbers ("2796") carry no owner and are only used for file matching.</summary>
    public static Dictionary<string, string> OwnerMap(string? last4)
    {
        var map = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(last4 ?? "", @"([A-Za-z]+):(\d{4,6})"))
        {
            string? who = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "aryn" => "Aryn",
                "mel" or "melissa" => "Mel",
                _ => null,
            };
            if (who != null) map[m.Groups[2].Value] = who;
        }
        return map;
    }

    private static ImportResult Insert(long cardId, string fileName, List<ParsedRow> parsed, List<string> warnings)
    {
        using var con = Db.Open();
        var cardInfo = con.QueryFirstOrDefault<(string? bucket, string? last4)>(
            "SELECT default_bucket, last4 FROM cards WHERE id = @id", new { id = cardId });
        string cardDefault = cardInfo.bucket ?? "Shared";
        var owners = OwnerMap(cardInfo.last4);

        int added = 0, dupes = 0, review = 0, skippedPayments = 0, matchedPending = 0;
        var payments = new List<PaymentSeen>();

        // Bank-account endings from Settings: if a payment row carries one of them,
        // we know whose account made that payment.
        string? arynBank = con.QueryFirstOrDefault<string>("SELECT v FROM meta WHERE k = 'bank_last4_aryn'");
        string? melBank  = con.QueryFirstOrDefault<string>("SELECT v FROM meta WHERE k = 'bank_last4_mel'");

        using var tx = con.BeginTransaction();
        foreach (var p in parsed)
        {
            if (p.LooksLikePayment)
            {
                skippedPayments++;
                string searchable = p.Raw.Length > 0 ? p.Raw : p.Description;
                string? payWho = null;
                if (!string.IsNullOrWhiteSpace(arynBank) && searchable.Contains(arynBank)) payWho = "Aryn";
                else if (!string.IsNullOrWhiteSpace(melBank) && searchable.Contains(melBank)) payWho = "Mel";
                payments.Add(new PaymentSeen((p.TxnDate ?? p.PostDate)?.ToString("yyyy-MM-dd"),
                                             Math.Round(Math.Abs(p.Amount), 2), p.Description, payWho));

                // If this is a substantial payment, we can assume charges before it are 'done'
                // for the purpose of the current statement review.
                continue;
            }

            string hash = Hash(cardId, p.TxnDate ?? p.PostDate, p.Description, p.Amount);
            bool exists = con.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM txns WHERE hash = @h", new { h = hash }, tx) > 0;
            if (exists) { dupes++; continue; }

            // A charge synced from the bank website earlier ("pending")? The official CSV row
            // silently takes over: real description/dates/hash, but the bucket + note you
            // already set are kept.
            var pendingId = con.QueryFirstOrDefault<long?>("""
                SELECT id FROM txns
                WHERE card_id = @cardId AND settled_id IS NULL AND source = 'sync'
                  AND ABS(amount - @amount) < 0.01
                  AND ABS(julianday(COALESCE(txn_date, post_date)) - julianday(@d)) <= 5
                LIMIT 1
                """, new { cardId, amount = Math.Round(p.Amount, 2),
                           d = (p.TxnDate ?? p.PostDate)?.ToString("yyyy-MM-dd") }, tx);
            if (pendingId != null)
            {
                con.Execute("""
                    UPDATE txns SET txn_date = @txnDate, post_date = @postDate, description = @desc,
                                    category = @cat, hash = @hash, source = @source
                    WHERE id = @id
                    """, new
                    {
                        id = pendingId.Value,
                        txnDate = p.TxnDate?.ToString("yyyy-MM-dd"),
                        postDate = p.PostDate?.ToString("yyyy-MM-dd"),
                        desc = p.Description,
                        cat = p.Category,
                        hash,
                        source = $"import:{fileName}",
                    }, tx);
                matchedPending++;
                continue;
            }

            // Whose number swiped? That person becomes the fallback bucket (confident
            // merchant rules and recurring-amount matches still take precedence).
            string rowDefault = cardDefault;
            string rowDigits = new string(p.CardNo.Where(char.IsDigit).ToArray());
            if (rowDigits.Length >= 4)
                foreach (var (digits, who) in owners)
                    if (rowDigits.EndsWith(digits) || digits.EndsWith(rowDigits))
                    { rowDefault = who; break; }

            string merchant = NormalizeMerchant(p.Description);
            var (bucket, needsReview, inheritedNote) =
                PredictSmart(con, cardId, merchant, p.Amount, rowDefault);
            if (p.Amount < 0) needsReview = true;   // refunds always deserve a look

            // App-set flags: things a human should glance at, with a hover-able reason.
            string? flagReason = null;
            if (Regex.IsMatch(p.Description, @"rent|housing", RegexOptions.IgnoreCase))
                flagReason = "Rent in-and-out — should net to zero with its twin row";
            else if (Regex.IsMatch(p.Description, @"late.*fee|interest charge", RegexOptions.IgnoreCase))
                flagReason = "Bank fee/interest — exists because a balance was carried";
            else if (p.Amount < 0)
                flagReason = "Refund — double-check who gets the credit";

            con.Execute("""
                INSERT INTO txns (card_id, txn_date, post_date, description, category, amount,
                                  bucket, note, needs_review, source, hash, flag_reason, flag_by)
                VALUES (@cardId, @txnDate, @postDate, @desc, @cat, @amount,
                        @bucket, @note, @review, @source, @hash, @flagReason,
                        CASE WHEN @flagReason IS NULL THEN NULL ELSE 'app' END)
                """,
                new
                {
                    cardId,
                    txnDate = p.TxnDate?.ToString("yyyy-MM-dd"),
                    postDate = p.PostDate?.ToString("yyyy-MM-dd"),
                    desc = p.Description,
                    cat = p.Category,
                    amount = Math.Round(p.Amount, 2),
                    bucket,
                    note = inheritedNote,
                    review = needsReview ? 1 : 0,
                    // Pending rows behave like web-synced charges: the posted version
                    // replaces them automatically on a later import.
                    source = p.Pending ? "sync" : $"import:{fileName}",
                    hash,
                    flagReason,
                }, tx);
            added++;
            if (needsReview) review++;
        }
        tx.Commit();
        return new ImportResult(added, dupes, review, skippedPayments, warnings, payments, matchedPending);
    }
}
