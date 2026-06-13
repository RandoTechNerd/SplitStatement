using System.Text;
using System.Text.Json;
using Dapper;

namespace MoneySplit;

// Image is base64 (no data: prefix); ImageType is the MIME, e.g. "image/png".
public record ChatMsg(string Role, string Content, string? Image = null, string? ImageType = null);

/// <summary>
/// The optional in-app assistant: a tool-using agent backed by the user's own Anthropic
/// API key (Settings). It can read cards/transactions/settlements/payments and make the
/// same safe edits the UI offers (buckets, notes, flags, balances, new cards). The app
/// is fully functional without it — this only augments investigation work.
/// </summary>
public static class AiAgent
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>The chat/vision-capable model ids this key can actually call.</summary>
    public static async Task<List<string>> ListModels(string provider, string apiKey)
    {
        if (provider == "google")
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}&pageSize=200");
            using var res = await Http.SendAsync(req);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var models))
                foreach (var m in models.EnumerateArray())
                {
                    string id = (m.GetProperty("name").GetString() ?? "").Replace("models/", "");
                    bool gen = m.TryGetProperty("supportedGenerationMethods", out var sm) &&
                               sm.EnumerateArray().Any(x => x.GetString() == "generateContent");
                    if (gen && id.StartsWith("gemini") && !id.Contains("embedding") &&
                        !id.Contains("tts") && !id.Contains("image") && !id.Contains("robotics") &&
                        !id.Contains("computer-use"))
                        list.Add(id);
                }
            return list;
        }
        else
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=100");
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            using var res = await Http.SendAsync(req);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data))
                foreach (var m in data.EnumerateArray())
                    if (m.TryGetProperty("id", out var idEl)) list.Add(idEl.GetString()!);
            return list;
        }
    }

    public static async Task<string> Chat(List<ChatMsg> history, string provider, string apiKey,
                                          string model, string personA, string personB, int maxSteps = 16)
    {
        var clean = history.Where(m => !string.IsNullOrWhiteSpace(m.Content) || m.Image != null).ToList();
        if (clean.Count == 0) return "Ask me something about the cards.";

        string system = $"""
            You are the built-in assistant of SplitStatement, a local app where {personA} and {personB}
            split credit-card charges. Buckets: 'Shared' (split 50/50), 'Aryn' (= {personA}),
            'Mel' (= {personB}), 'Skip' (excluded). Amounts: positive = charge, negative = refund.
            Always look at real data with tools before answering. Investigate like an accountant:
            compare the entered current balance against open charges, check detected payments,
            hunt for missing or duplicated rows, and explain discrepancies with exact numbers.
            You can email with send_email when asked (e.g. "email {personB} about this charge"):
            include the card, date, merchant, amount, and your analysis in a friendly plain-text body.
            For a FRAUD CHECK: pull recent open transactions across all cards; look for unfamiliar
            merchants, duplicates, weird amounts, foreign locations that don't match known travel;
            research suspicious merchant names{(provider == "anthropic" ? " with web_search" : "")};
            then report findings ranked by suspicion with a clear "looks fine" for the rest.
            For most overview/insight questions, call get_digest FIRST — it's a pre-sorted local
            summary (per-card status + open spend by category with reward-fit flags already done),
            so you usually need little more. For deeper REWARDS OPTIMIZATION add get_spending_stats. Reason about which
            card each category SHOULD ride on (Amazon/Whole Foods → Prime Visa 5%; groceries &
            streaming → Amex Blue Cash; rent → BILT points; flat-rate cards for the rest) and
            quantify missed cashback in dollars. Compare recent spend volume against signup-bonus
            minimum spends{(provider == "anthropic" ? " — use web_search for current public offers" : "")}
            (e.g. "$6k over 2 months would have cleared a $4k/3mo minimum for a ~$750 bonus").
            Give short, numbered, dollar-quantified recommendations, then a 3-sentence monthly
            narrative of notable changes. The user can lock bucket rules by asking — use
            set_merchant_rule for "X is always {personB}'s" style requests.
            If the user attaches a SCREENSHOT (e.g. a bank's pending-charges page or a receipt),
            read it carefully and cross-reference it against the imported data — call out charges
            in the image that are missing from the app, or amounts that don't match.
            BE EFFICIENT WITH TOOLS: call each tool at most once unless you genuinely need new
            data; get_spending_stats already returns cards, categories, and top merchants in a
            single call, so don't re-query it. Gather what you need in a few calls, then answer.
            Refer to people as {personA} and {personB}. Be concise and concrete.
            """;

        return provider == "google"
            ? await ChatGemini(clean, apiKey, model, system, maxSteps)
            : await ChatAnthropic(clean, apiKey, model, system, maxSteps);
    }

    private static async Task<string> ChatAnthropic(List<ChatMsg> history, string apiKey,
                                                    string model, string system, int maxSteps)
    {
        var messages = history
            .Select(m => (object)new { role = m.Role == "assistant" ? "assistant" : "user", content = (object)m.Content })
            .ToList();
        // Our tools + Anthropic's server-side web search (merchant lookups, fraud research).
        var tools = ToolSpecs.Select(t => (object)new { name = t.Name, description = t.Desc, input_schema = t.Schema })
            .Append(new { type = "web_search_20250305", name = "web_search", max_uses = 4 } as object)
            .ToArray();

        for (int turn = 0; turn < maxSteps; turn++)
        {
            var body = new { model, max_tokens = 1500, system, messages, tools };
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var res = await Http.SendAsync(req);
            string json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                return $"AI request failed ({(int)res.StatusCode}): {json[..Math.Min(300, json.Length)]}";

            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement.GetProperty("content");
            string stop = doc.RootElement.GetProperty("stop_reason").GetString() ?? "";

            if (stop != "tool_use")
            {
                var sb = new StringBuilder();
                foreach (var block in content.EnumerateArray())
                    if (block.GetProperty("type").GetString() == "text")
                        sb.Append(block.GetProperty("text").GetString());
                return sb.Length > 0 ? sb.ToString() : "(no reply)";
            }

            messages.Add(new { role = "assistant",
                               content = JsonSerializer.Deserialize<object>(content.GetRawText())! });
            var results = new List<object>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.GetProperty("type").GetString() != "tool_use") continue;
                string output;
                try { output = RunTool(block.GetProperty("name").GetString()!,
                                       block.GetProperty("input").GetRawText()); }
                catch (Exception ex) { output = "tool error: " + ex.Message; }
                if (output.Length > 4000) output = output[..4000] + "…(truncated)";
                results.Add(new { type = "tool_result",
                                  tool_use_id = block.GetProperty("id").GetString(), content = output });
            }
            messages.Add(new { role = "user", content = results });
        }
        return "That needed more digging than one turn allows. Ask me something narrower " +
               "(e.g. just the rewards check, or just one card) and I'll nail it.";
    }

    private static async Task<string> ChatGemini(List<ChatMsg> history, string apiKey,
                                                 string model, string system, int maxSteps)
    {
        var contents = history.Select(m =>
        {
            var parts = new List<object>();
            if (m.Image != null)
                parts.Add(new { inlineData = new { mimeType = m.ImageType ?? "image/png", data = m.Image } });
            if (!string.IsNullOrWhiteSpace(m.Content)) parts.Add(new { text = m.Content });
            return (object)new { role = m.Role == "assistant" ? "model" : "user", parts };
        }).ToList();
        var tools = new object[]
        {
            new { functionDeclarations = ToolSpecs.Select(t =>
                (object)new { name = t.Name, description = t.Desc, parameters = t.Schema }).ToArray() },
        };

        for (int turn = 0; turn < maxSteps; turn++)
        {
            var body = new
            {
                system_instruction = new { parts = new object[] { new { text = system } } },
                contents,
                tools,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}");
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var res = await Http.SendAsync(req);
            string json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                return $"AI request failed ({(int)res.StatusCode}): {json[..Math.Min(300, json.Length)]}";

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0)
                return "Gemini returned no answer (possibly blocked content).";
            var content = cands[0].GetProperty("content");

            var calls = new List<(string name, string args)>();
            var text = new StringBuilder();
            if (content.TryGetProperty("parts", out var parts))
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("functionCall", out var fc))
                        calls.Add((fc.GetProperty("name").GetString()!,
                                   fc.TryGetProperty("args", out var a) ? a.GetRawText() : "{}"));
                    else if (part.TryGetProperty("text", out var t))
                        text.Append(t.GetString());
                }

            if (calls.Count == 0) return text.Length > 0 ? text.ToString() : "(no reply)";

            contents.Add(JsonSerializer.Deserialize<object>(content.GetRawText())!);
            contents.Add(new
            {
                role = "user",
                parts = calls.Select(c =>
                {
                    string output;
                    try { output = RunTool(c.name, c.args); }
                    catch (Exception ex) { output = "tool error: " + ex.Message; }
                    if (output.Length > 4000) output = output[..4000] + "…(truncated)";
                    return (object)new { functionResponse = new { name = c.name, response = new { result = output } } };
                }).ToArray(),
            });
        }
        return "That needed more digging than one turn allows. Ask me something narrower " +
               "(e.g. just the rewards check, or just one card) and I'll nail it.";
    }

    // ---------- tools ----------
    private static object Schema(params (string name, string type, string desc)[] props) => new
    {
        type = "object",
        properties = props.ToDictionary(p => p.name, p => (object)new { type = p.type, description = p.desc }),
    };

    private record ToolSpec(string Name, string Desc, object Schema);

    private static readonly ToolSpec[] ToolSpecs =
    {
        new("list_cards", "All cards with open totals, parts, balances, discrepancies, review counts.",
            Schema()),
        new("get_transactions", "Transactions for one card.",
            Schema(("card", "string", "card name (fuzzy)"), ("view", "string", "open|settled|all (default open)"))),
        new("search_transactions", "Search all cards by text and/or exact amount.",
            Schema(("text", "string", "description/note contains"), ("amount", "number", "exact amount, charge positive"))),
        new("recent_transactions", "Recent transactions across ALL cards in ONE call (last N days, default 35), with card name and category. Use this instead of calling get_transactions per card.",
            Schema(("days", "number", "look-back window in days (default 35)"), ("category", "string", "optional: only rows whose category/description contains this"))),
        new("update_transaction", "Set bucket/note/flag on a transaction by id.",
            Schema(("id", "number", "transaction id"), ("bucket", "string", "Shared|Aryn|Mel|Skip"),
                   ("note", "string", "note text"), ("flag_reason", "string", "flag with this reason"))),
        new("add_card", "Create a new card.",
            Schema(("name", "string", "card name"), ("due_day", "number", "due day of month 1-31"))),
        new("set_current_balance", "Enter the bank's current balance for a card (discrepancy check).",
            Schema(("card", "string", "card name (fuzzy)"), ("amount", "number", "current balance"))),
        new("get_settlements", "Settle-up history for a card incl. payment lines.",
            Schema(("card", "string", "card name (fuzzy)"))),
        new("get_payments", "Detected card payments (from statements) for a card.",
            Schema(("card", "string", "card name (fuzzy)"))),
        new("send_email", "Send a plain-text email via the configured SMTP. Defaults to the household address. Only when the user asks for an email.",
            Schema(("subject", "string", "subject line"), ("body", "string", "plain-text body"),
                   ("to", "string", "recipient address (optional)"))),
        new("get_spending_stats", "Aggregated spend for insights & rewards optimization: per card per month (6 months), per category, top merchants.",
            Schema()),
        new("get_digest", "PRE-SORTED one-call overview the app computes locally: every card's open total/balance/discrepancy/due, open spend grouped by category (with which cards it's on), and a reward-fit check flagging categories riding a non-optimal card. Start here — it replaces many separate lookups.",
            Schema()),
        new("set_merchant_rule", "Permanent natural-language rule: this merchant ALWAYS goes to one bucket. Optionally re-buckets matching open charges too.",
            Schema(("merchant", "string", "merchant keyword, e.g. CHEWY"), ("bucket", "string", "Shared|Aryn|Mel"),
                   ("apply_to_open", "boolean", "also update current open charges that match"))),
    };

    private static string NormalizeMerchantSafe(string desc)
    {
        try { return Importer.NormalizeMerchant(desc); } catch { return desc; }
    }

    // Which card-type each spend category SHOULD ride on (keyword → reward note). The app
    // pre-computes the fit so the AI doesn't have to reason it out from scratch.
    private static readonly (string cat, string wantCard, string reward)[] RewardMap =
    {
        ("grocer", "amex blue cash", "6% groceries"),
        ("whole foods", "prime / amz", "5% Whole Foods/Amazon"),
        ("amazon", "prime / amz", "5% Amazon"),
        ("gas", "amex blue cash", "3% gas"),
        ("fuel", "amex blue cash", "3% gas"),
        ("stream", "amex blue cash", "6% streaming"),
        ("rent", "bilt", "rent points, no fee"),
        ("housing", "bilt", "rent points, no fee"),
        ("travel", "venture / cap 1", "miles on travel"),
        ("transit", "venture / cap 1", "transit category"),
        ("dining", "venture / cap 1", "dining bonus"),
        ("restaurant", "venture / cap 1", "dining bonus"),
    };

    /// <summary>One compact, pre-sorted JSON: per-card status + open spend by category with a
    /// reward-fit flag. Computed in SQL/C# so the model reads instead of digging.</summary>
    private static string BuildDigest(Microsoft.Data.Sqlite.SqliteConnection con)
    {
        var cards = con.Query("""
            SELECT c.name, c.due_day AS dueDay, c.default_payer AS payer, c.stmt_balance AS currentBalance, c.note,
                   ROUND(COALESCE((SELECT SUM(amount) FROM txns t WHERE t.card_id=c.id AND t.settled_id IS NULL AND t.bucket!='Skip'),0),2) AS openTotal,
                   (SELECT COUNT(*) FROM txns t WHERE t.card_id=c.id AND t.settled_id IS NULL AND t.needs_review=1) AS reviewCount
            FROM cards c WHERE c.archived = 0 ORDER BY openTotal DESC
            """).ToList();

        // Open spend grouped by category, with the cards each category currently rides.
        var catRows = con.Query<(string? category, string card, double amount)>("""
            SELECT t.category, c.name AS card, t.amount
            FROM txns t JOIN cards c ON c.id = t.card_id
            WHERE t.settled_id IS NULL AND t.amount > 0 AND t.bucket != 'Skip'
            """).ToList();

        var byCat = catRows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.category) ? "(uncategorized)" : r.category!.Trim())
            .Select(g =>
            {
                double total = Math.Round(g.Sum(x => x.amount), 2);
                var cardsUsed = g.GroupBy(x => x.card)
                    .Select(cg => new { card = cg.Key, amount = Math.Round(cg.Sum(x => x.amount), 2) })
                    .OrderByDescending(x => x.amount).ToList();
                var fit = RewardMap.FirstOrDefault(m => g.Key.ToLowerInvariant().Contains(m.cat));
                bool onRightCard = fit.wantCard != null &&
                    cardsUsed.Any(c => c.card.ToLowerInvariant().Split(' ', '/')
                        .Any(w => fit.wantCard.Contains(w) && w.Length > 2));
                return new
                {
                    category = g.Key, total, cards = cardsUsed,
                    bestCardType = fit.wantCard, bestReward = fit.reward,
                    rewardFlag = fit.wantCard != null && !onRightCard
                        ? $"category may earn more on a {fit.wantCard} card ({fit.reward})" : null,
                };
            })
            .OrderByDescending(x => x.total).ToList();

        return JsonSerializer.Serialize(new { cards, openSpendByCategory = byCat });
    }

    private static long CardId(string name)
    {
        using var con = Db.Open();
        var id = con.QueryFirstOrDefault<long?>(
            "SELECT id FROM cards WHERE name LIKE '%' || @n || '%' AND archived = 0 ORDER BY LENGTH(name) LIMIT 1",
            new { n = name });
        return id ?? throw new Exception($"no card matching '{name}'");
    }

    private static string RunTool(string name, string inputJson)
    {
        using var doc = JsonDocument.Parse(inputJson);
        var input = doc.RootElement;
        string? Str(string k) => input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? Num(string k) => input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        using var con = Db.Open();
        switch (name)
        {
            case "list_cards":
                return JsonSerializer.Serialize(con.Query("""
                    SELECT c.name, c.due_day, c.default_payer, c.stmt_balance AS currentBalance,
                           ROUND(COALESCE((SELECT SUM(amount) FROM txns t WHERE t.card_id=c.id AND t.settled_id IS NULL AND t.bucket!='Skip'),0),2) AS openTotal,
                           (SELECT COUNT(*) FROM txns t WHERE t.card_id=c.id AND t.settled_id IS NULL AND t.needs_review=1) AS reviewCount
                    FROM cards c WHERE c.archived = 0
                    """));
            case "get_transactions":
            {
                long id = CardId(Str("card") ?? "");
                string view = Str("view") ?? "open";
                string where = view == "all" ? "" : view == "settled" ? "AND settled_id IS NOT NULL" : "AND settled_id IS NULL";
                return JsonSerializer.Serialize(con.Query($"""
                    SELECT id, txn_date, description, category, amount, bucket, note, needs_review, flag_reason
                    FROM txns WHERE card_id = @id {where}
                    ORDER BY COALESCE(txn_date, post_date) DESC LIMIT 90
                    """, new { id }));
            }
            case "search_transactions":
            {
                string? text = Str("text"); double? amt = Num("amount");
                return JsonSerializer.Serialize(con.Query("""
                    SELECT t.id, c.name AS card, t.txn_date, t.description, t.amount, t.bucket, t.note, t.settled_id
                    FROM txns t JOIN cards c ON c.id = t.card_id
                    WHERE (@text IS NULL OR t.description LIKE '%' || @text || '%' OR t.note LIKE '%' || @text || '%')
                      AND (@amt IS NULL OR ABS(ABS(t.amount) - ABS(@amt)) < 0.005)
                    ORDER BY COALESCE(t.txn_date, t.post_date) DESC LIMIT 60
                    """, new { text, amt }));
            }
            case "get_digest":
                return BuildDigest(con);
            case "recent_transactions":
            {
                int days = (int)(Num("days") ?? 35);
                string? cat = Str("category");
                return JsonSerializer.Serialize(con.Query("""
                    SELECT t.id, c.name AS card, t.txn_date, t.description, t.category, t.amount,
                           t.bucket, t.settled_id AS settled
                    FROM txns t JOIN cards c ON c.id = t.card_id
                    WHERE COALESCE(t.txn_date, t.post_date) >= date('now', '-' || @days || ' days')
                      AND (@cat IS NULL OR t.category LIKE '%' || @cat || '%' OR t.description LIKE '%' || @cat || '%')
                    ORDER BY COALESCE(t.txn_date, t.post_date) DESC LIMIT 250
                    """, new { days, cat }));
            }
            case "update_transaction":
            {
                long id = (long)(Num("id") ?? throw new Exception("id required"));
                string? bucket = Str("bucket"); string? note = Str("note"); string? flag = Str("flag_reason");
                if (bucket != null)
                {
                    con.Execute("UPDATE txns SET bucket = @bucket, needs_review = 0 WHERE id = @id", new { bucket, id });
                    var desc = con.QueryFirstOrDefault<string>("SELECT description FROM txns WHERE id = @id", new { id });
                    if (desc != null) Importer.LearnRule(con, Importer.NormalizeMerchant(desc), bucket);
                }
                if (note != null) con.Execute("UPDATE txns SET note = @note WHERE id = @id", new { note, id });
                if (flag != null) con.Execute("UPDATE txns SET flag_by = 'app', flag_reason = @flag WHERE id = @id", new { flag, id });
                return "updated";
            }
            case "add_card":
                con.Execute("INSERT INTO cards (name, due_day) VALUES (@n, @d)",
                            new { n = Str("name") ?? throw new Exception("name required"), d = (int?)Num("due_day") });
                return "card added";
            case "set_current_balance":
                con.Execute("UPDATE cards SET stmt_balance = @b, stmt_balance_at = datetime('now') WHERE id = @id",
                            new { b = Num("amount"), id = CardId(Str("card") ?? "") });
                return "balance set";
            case "get_settlements":
                return JsonSerializer.Serialize(con.Query("""
                    SELECT label, settled_at, shared_total, mel_total, aryn_total, mel_part, aryn_part, payments, note
                    FROM settlements WHERE card_id = @id ORDER BY settled_at DESC LIMIT 12
                    """, new { id = CardId(Str("card") ?? "") }));
            case "get_payments":
            {
                long id = CardId(Str("card") ?? "");
                return con.QueryFirstOrDefault<string>("SELECT v FROM meta WHERE k = 'detected_pay:' || @id", new { id }) ?? "[]";
            }
            case "send_email":
            {
                var (ok, error) = Mailer.Send(Str("subject") ?? "SplitStatement", Str("body") ?? "", Str("to"));
                return ok ? "email sent" : $"email failed: {error}";
            }
            case "get_spending_stats":
            {
                var byCardMonth = con.Query("""
                    SELECT c.name AS card, substr(COALESCE(t.txn_date, t.post_date), 1, 7) AS month,
                           ROUND(SUM(CASE WHEN t.amount > 0 THEN t.amount ELSE 0 END), 2) AS spend
                    FROM txns t JOIN cards c ON c.id = t.card_id
                    WHERE COALESCE(t.txn_date, t.post_date) >= date('now', '-6 months') AND t.bucket != 'Skip'
                    GROUP BY c.name, month HAVING spend > 0 ORDER BY month DESC, spend DESC
                    """);
                var byCategory = con.Query("""
                    SELECT COALESCE(category, '(uncategorized)') AS category, ROUND(SUM(amount), 2) AS spend
                    FROM txns
                    WHERE COALESCE(txn_date, post_date) >= date('now', '-6 months') AND amount > 0 AND bucket != 'Skip'
                    GROUP BY category ORDER BY spend DESC LIMIT 15
                    """);
                var raw = con.Query<(string desc, double amount)>("""
                    SELECT description, amount FROM txns
                    WHERE COALESCE(txn_date, post_date) >= date('now', '-6 months') AND amount > 0 AND bucket != 'Skip'
                    """);
                var topMerchants = raw.GroupBy(r => NormalizeMerchantSafe(r.desc))
                    .Select(g => new { merchant = g.Key, spend = Math.Round(g.Sum(x => x.amount), 2), count = g.Count() })
                    .OrderByDescending(x => x.spend).Take(20);
                return JsonSerializer.Serialize(new { byCardMonth, byCategory, topMerchants });
            }
            case "set_merchant_rule":
            {
                string m = (Str("merchant") ?? throw new Exception("merchant required")).ToUpperInvariant();
                string bucket = Str("bucket") ?? throw new Exception("bucket required");
                if (bucket is not ("Shared" or "Aryn" or "Mel")) throw new Exception("bucket must be Shared|Aryn|Mel");
                bool applyOpen = input.TryGetProperty("apply_to_open", out var ao) && ao.ValueKind == JsonValueKind.True;

                var keys = con.Query<string>(
                    "SELECT merchant FROM rules WHERE merchant LIKE '%' || @m || '%'", new { m }).ToList();
                if (keys.Count == 0) keys.Add(Importer.NormalizeMerchant(m));
                foreach (var k in keys)
                    con.Execute("""
                        INSERT INTO rules (merchant, shared_n, mel_n, aryn_n) VALUES (@k, @s, @me, @a)
                        ON CONFLICT(merchant) DO UPDATE SET shared_n = @s, mel_n = @me, aryn_n = @a
                        """, new { k, s = bucket == "Shared" ? 99 : 0, me = bucket == "Mel" ? 99 : 0, a = bucket == "Aryn" ? 99 : 0 });
                int updated = 0;
                if (applyOpen)
                    updated = con.Execute("""
                        UPDATE txns SET bucket = @bucket, needs_review = 0
                        WHERE settled_id IS NULL AND UPPER(description) LIKE '%' || @m || '%'
                        """, new { bucket, m });
                return $"rule locked: [{string.Join(", ", keys)}] → {bucket}" +
                       (applyOpen ? $"; {updated} open charge(s) re-bucketed" : "");
            }
            default: return "unknown tool";
        }
    }
}
