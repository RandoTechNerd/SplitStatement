using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Dapper;
using Makaretu.Dns;
using MoneySplit;

var builder = WebApplication.CreateBuilder(args);
// Dual-stack (* = IPv4 + IPv6): phones try IPv6 first, and an unreachable v6 costs seconds
// of fallback delay. Always on 5275; also take port 80 if free so "split.local" needs no port.
var urls = new List<string> { "http://*:5275" };
try
{
    var probe = new TcpListener(IPAddress.Any, 80);
    probe.Start();
    probe.Stop();
    urls.Add("http://*:80");
}
catch { /* something else owns port 80 — 5275 still works */ }
builder.WebHost.UseUrls(urls.ToArray());
var app = builder.Build();

// Running from the project (dotnet run): keep the db in the project's data\ folder so it's
// easy to find and back up. Running a published exe from elsewhere: fall back to %APPDATA%.
string dataDir = Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"))
    ? Path.Combine(Directory.GetCurrentDirectory(), "data")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MoneySplit");
Directory.CreateDirectory(dataDir);
Db.Init(Path.Combine(dataDir, "moneysplit.db"));
Console.WriteLine($"  Database: {Path.Combine(dataDir, "moneysplit.db")}");

// First run anywhere (including a shared blank copy): a sensible statements folder.
if (GetMeta("statements_folder") == null)
{
    string def = Path.Combine(dataDir, "statements");
    Directory.CreateDirectory(def);
    SetMeta("statements_folder", def);
}
// Migration: the single ai_key slot became per-provider slots.
if (GetMeta("ai_key") is string legacyKey && legacyKey.Length > 0 && GetMeta("ai_key_anthropic") == null)
    SetMeta("ai_key_anthropic", legacyKey);

// Backfill card types for cards created before the catalog existed — classify by name + file hint.
using (var con0 = Db.Open())
{
    var untyped = con0.Query<(long id, string name, string? hint)>(
        "SELECT id, name, file_hint FROM cards WHERE card_type IS NULL");
    foreach (var c in untyped)
    {
        var t = CardCatalog.Detect(c.name, c.hint);
        if (t == null) continue;
        string mergedNote = CardCatalog.ApplyTags(
            con0.QueryFirstOrDefault<string>("SELECT note FROM cards WHERE id = @id", new { id = c.id }), t.Key);
        con0.Execute("""
            UPDATE cards SET card_type = @key, rewards = @rewards, note = @note,
                             color = CASE WHEN color IS NULL OR color = 'auto' THEN @skin ELSE color END
            WHERE id = @id
            """, new { key = t.Key, rewards = t.Rewards, note = mergedNote, skin = $"skin:{t.Skin}", id = c.id });
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();

// ---------- state: everything the dashboard needs in one call ----------
app.MapGet("/api/state", () =>
{
    using var con = Db.Open();
    var cards = con.Query<CardRow>("""
        SELECT id, name, due_day AS DueDay, color, default_bucket AS DefaultBucket,
               default_payer AS DefaultPayer, carry_mel AS CarryMel, carry_aryn AS CarryAryn,
               stmt_balance AS StmtBalance, stmt_balance_at AS StmtBalanceAt, note,
               last4 AS Last4, card_type AS CardType, rewards AS Rewards
        FROM cards WHERE archived = 0 ORDER BY sort, name
        """).ToList();

    var result = new List<object>();
    double netArynOwesMel = 0;
    foreach (var c in cards)
    {
        var t = con.QueryFirst<(double shared, double mel, double aryn, long n, long review)>("""
            SELECT COALESCE(SUM(CASE WHEN bucket='Shared' THEN amount END), 0),
                   COALESCE(SUM(CASE WHEN bucket='Mel'    THEN amount END), 0),
                   COALESCE(SUM(CASE WHEN bucket='Aryn'   THEN amount END), 0),
                   COUNT(CASE WHEN bucket != 'Skip' THEN 1 END),
                   COALESCE(SUM(needs_review), 0)
            FROM txns WHERE card_id = @id AND settled_id IS NULL
            """, new { id = c.Id });

        double melPart = Math.Round(t.shared / 2 + t.mel + c.CarryMel, 2);
        double arynPart = Math.Round(t.shared / 2 + t.aryn + c.CarryAryn, 2);
        double openTotal = Math.Round(t.shared + t.mel + t.aryn, 2);
        double? discrepancy = c.StmtBalance is double b ? Math.Round(b - openTotal, 2) : null;

        // Whoever fronts the card payment is owed the other's part.
        if (c.DefaultPayer == "Mel") netArynOwesMel += arynPart;
        else if (c.DefaultPayer == "Aryn") netArynOwesMel -= melPart;

        result.Add(new
        {
            c.Id, c.Name, c.DueDay, c.Color, c.DefaultBucket, c.DefaultPayer,
            c.CarryMel, c.CarryAryn, c.StmtBalance, c.StmtBalanceAt, c.Note, c.Last4,
            c.CardType, c.Rewards,
            sharedTotal = Math.Round(t.shared, 2),
            melTotal = Math.Round(t.mel, 2),
            arynTotal = Math.Round(t.aryn, 2),
            melPart, arynPart, openTotal, discrepancy,
            txnCount = t.n,
            reviewCount = t.review,
            dueInDays = DueInDays(c.DueDay),
            lastSettledAt = con.QueryFirstOrDefault<string>(
                "SELECT settled_at FROM settlements WHERE card_id = @id ORDER BY settled_at DESC LIMIT 1",
                new { id = c.Id }),
            lastImportAt = con.QueryFirstOrDefault<string>(
                "SELECT MAX(created_at) FROM txns WHERE card_id = @id AND source LIKE 'import:%'",
                new { id = c.Id }),
        });
    }
    return Results.Json(new { cards = result, netArynOwesMel = Math.Round(netArynOwesMel, 2) });
});

// ---------- transactions ----------
app.MapGet("/api/cards/{id:long}/txns", (long id, string? view) =>
{
    using var con = Db.Open();
    string where = view switch
    {
        "all" => "card_id = @id",
        "settled" => "card_id = @id AND settled_id IS NOT NULL",
        _ => "card_id = @id AND settled_id IS NULL",
    };
    var rows = con.Query($"""
        SELECT id, txn_date AS txnDate, post_date AS postDate, description, category, amount,
               bucket, note, needs_review AS needsReview, settled_id AS settledId, source,
               flag_reason AS flagReason, flag_by AS flagBy, receipt
        FROM txns WHERE {where}
        ORDER BY settled_id IS NOT NULL, COALESCE(txn_date, post_date) DESC, id DESC
        """, new { id });
    return Results.Json(rows);
});

app.MapPatch("/api/txns/{id:long}", async (long id, HttpRequest req) =>
{
    var body = await req.ReadFromJsonAsync<TxnPatch>() ?? new TxnPatch();
    using var con = Db.Open();
    var existing = con.QueryFirstOrDefault<(string description, string bucket)?>(
        "SELECT description, bucket FROM txns WHERE id = @id", new { id });
    if (existing == null) return Results.NotFound();

    if (body.Bucket != null)
        con.Execute("UPDATE txns SET bucket = @b, needs_review = 0 WHERE id = @id",
                    new { b = body.Bucket, id });
    if (body.Note != null)
        con.Execute("UPDATE txns SET note = @n WHERE id = @id", new { n = body.Note, id });
    if (body.Confirm == true)
        con.Execute("UPDATE txns SET needs_review = 0 WHERE id = @id", new { id });
    if (body.Flag == false)
        con.Execute("UPDATE txns SET flag_by = NULL, flag_reason = NULL WHERE id = @id", new { id });
    else if (body.Flag == true)
        con.Execute("UPDATE txns SET flag_by = @by, flag_reason = @reason WHERE id = @id",
                    new { id, by = body.FlagBy ?? "Aryn", reason = body.FlagReason });

    // A human decision is the strongest signal — teach the merchant memory.
    if (body.Bucket != null || body.Confirm == true)
    {
        string bucket = body.Bucket ?? existing.Value.bucket;
        Importer.LearnRule(con, Importer.NormalizeMerchant(existing.Value.description), bucket);
    }
    return Results.Ok();
});

app.MapPost("/api/txns", async (HttpRequest req) =>
{
    var b = await req.ReadFromJsonAsync<ManualTxn>();
    if (b == null || b.CardId == 0 || string.IsNullOrWhiteSpace(b.Description))
        return Results.BadRequest();

    // Hash manual/synced rows the same way imports are hashed, so the official CSV
    // doesn't re-add the same charge later.
    DateTime? d = DateTime.TryParse(b.Date, out var parsed) ? parsed : null;
    string hash = Importer.Hash(b.CardId, d, b.Description, b.Amount);

    using var con = Db.Open();
    bool dup = con.ExecuteScalar<long>("SELECT COUNT(*) FROM txns WHERE hash = @hash", new { hash }) > 0;

    // Web-synced rows never hash-match the official statement rows (different dates/wording),
    // so for those, same amount within ±3 days of an existing real row = already have it.
    if (!dup && b.Source == "sync" && d != null)
        dup = con.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM txns
            WHERE card_id = @CardId AND ABS(amount - @Amount) < 0.005 AND source != 'sync'
              AND ABS(julianday(COALESCE(txn_date, post_date)) - julianday(@d)) <= 3
            """, new { b.CardId, b.Amount, d = d.Value.ToString("yyyy-MM-dd") }) > 0;

    if (dup) return Results.Json(new { duplicate = true });
    con.Execute("""
        INSERT INTO txns (card_id, txn_date, description, amount, bucket, note, source, hash, needs_review)
        VALUES (@CardId, @Date, @Description, @Amount, @Bucket, @Note, @Source, @hash, @review)
        """, new { b.CardId, Date = d?.ToString("yyyy-MM-dd"), b.Description, b.Amount,
                   b.Bucket, b.Note, b.Source, hash,
                   review = b.Source == "sync" ? 1 : 0 });   // web-pasted rows get a once-over
    return Results.Json(new { duplicate = false });
});

// Everything flagged for review, across all cards — feeds the Home screen queue.
app.MapGet("/api/review-queue", () =>
{
    using var con = Db.Open();
    var rows = con.Query("""
        SELECT t.id, t.card_id AS cardId, c.name AS cardName, t.txn_date AS txnDate,
               t.description, t.category, t.amount, t.bucket, t.note
        FROM txns t JOIN cards c ON c.id = t.card_id
        WHERE t.settled_id IS NULL AND t.needs_review = 1 AND c.archived = 0
        ORDER BY COALESCE(t.txn_date, t.post_date) DESC
        """);
    return Results.Json(rows);
});

// ---------- settings ----------
app.MapGet("/api/settings", () =>
{
    string? ip = null;
    try
    {
        ip = Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork &&
                                 !IPAddress.IsLoopback(a))?.ToString();
    }
    catch { /* localhost only */ }
    return Results.Json(new
    {
        statementsFolder = GetMeta("statements_folder") ?? "",
        remoteUrl = "http://split.local",
        remoteIp = ip == null ? null : $"http://{ip}:5275",
        arynBank = GetMeta("bank_last4_aryn") ?? "",
        melBank = GetMeta("bank_last4_mel") ?? "",
        blurList = GetMeta("creator_blur") ?? "fred hutch, kratom",
        personA = GetMeta("person_a") ?? "Aryn",
        personB = GetMeta("person_b") ?? "Mel",
        aiProvider = GetMeta("ai_provider") ?? "anthropic",
        aiKeySet = !string.IsNullOrEmpty(GetMeta($"ai_key_{GetMeta("ai_provider") ?? "anthropic"}")),
        aiModel = GetMeta("ai_model") ?? "",
        aiMaxSteps = GetMeta("ai_max_steps") ?? "16",
        // flash-latest tracks Google's newest free-tier flash; pro needs billing.
        aiAutoModel = (GetMeta("ai_provider") ?? "anthropic") == "google" ? "gemini-flash-latest" : "claude-sonnet-4-6",
        smtpHost = GetMeta("smtp_host") ?? "",
        smtpPort = GetMeta("smtp_port") ?? "587",
        smtpUser = GetMeta("smtp_user") ?? "",
        smtpPassSet = !string.IsNullOrEmpty(GetMeta("smtp_pass")),
        emailTo = GetMeta("email_to") ?? "",
        remindDays = GetMeta("remind_days") ?? "0",
        remindProgressive = GetMeta("remind_progressive") == "1",
        archiveAfterScan = GetMeta("archive_after_scan") == "1",
        archiveNaming = GetMeta("archive_naming") ?? "datecard",
    });
});

app.MapPost("/api/settings", async (HttpRequest req) =>
{
    var b = await req.ReadFromJsonAsync<SettingsBody>();
    if (!string.IsNullOrWhiteSpace(b?.StatementsFolder))
        SetMeta("statements_folder", b.StatementsFolder.Trim());
    if (b?.ArynBank != null) SetMeta("bank_last4_aryn", new string(b.ArynBank.Where(char.IsDigit).ToArray()));
    if (b?.MelBank != null) SetMeta("bank_last4_mel", new string(b.MelBank.Where(char.IsDigit).ToArray()));
    if (b?.BlurList != null) SetMeta("creator_blur", b.BlurList.Trim());
    if (!string.IsNullOrWhiteSpace(b?.PersonA)) SetMeta("person_a", b.PersonA.Trim());
    if (!string.IsNullOrWhiteSpace(b?.PersonB)) SetMeta("person_b", b.PersonB.Trim());
    if (!string.IsNullOrWhiteSpace(b?.AiProvider)) SetMeta("ai_provider", b.AiProvider.Trim().ToLowerInvariant());
    if (!string.IsNullOrWhiteSpace(b?.AiKey))
    {
        // Keys self-identify: route by shape so a paste can never land in the wrong provider,
        // and follow the key with the provider selection so it Just Works.
        string k = b.AiKey.Trim();
        string slot = k.StartsWith("sk-ant-") ? "anthropic"
                    : (k.StartsWith("AIza") || k.StartsWith("AQ.")) ? "google"
                    : GetMeta("ai_provider") ?? "anthropic";
        SetMeta($"ai_key_{slot}", k);
        SetMeta("ai_provider", slot);
    }
    if (b?.AiModel != null) SetMeta("ai_model", b.AiModel.Trim());   // "" = auto-pick best
    if (b?.AiMaxSteps != null) SetMeta("ai_max_steps", b.AiMaxSteps.Trim());
    if (b?.SmtpHost != null) SetMeta("smtp_host", b.SmtpHost.Trim());
    if (b?.SmtpPort != null) SetMeta("smtp_port", b.SmtpPort.Trim());
    if (b?.SmtpUser != null) SetMeta("smtp_user", b.SmtpUser.Trim());
    if (b?.SmtpPass != null) SetMeta("smtp_pass", b.SmtpPass.Replace(" ", "").Trim());   // app passwords paste with spaces
    if (b?.EmailTo != null) SetMeta("email_to", b.EmailTo.Trim());
    if (b?.RemindDays != null) SetMeta("remind_days", b.RemindDays.Trim());
    if (b?.RemindProgressive != null) SetMeta("remind_progressive", b.RemindProgressive == true ? "1" : "0");
    if (b?.ArchiveAfterScan != null) SetMeta("archive_after_scan", b.ArchiveAfterScan == true ? "1" : "0");
    if (b?.ArchiveNaming != null) SetMeta("archive_naming", b.ArchiveNaming.Trim());
    return Results.Ok();
});

// SMTP: send a themed test message showcasing the email design.
app.MapPost("/api/email/test", () =>
{
    string sections =
        Mailer.HeroBubble("It works", "<span style='font-size:26px;font-weight:700;color:#0b57d0;'>Email is wired up ✓</span><br><span style='font-size:13.5px;'>and every SplitStatement email looks like this.</span>") +
        Mailer.Bubble("Sample breakdown",
            Mailer.Row("Shared", "$123.45") + Mailer.Row("→ each", "$61.73") + Mailer.Row("Total", "$200.00", bold: true)) +
        Mailer.AlertBubble("Heads up", "Urgent things show up in amber cards like this one.");
    var (ok, error) = Mailer.SendHtml("SplitStatement — themed test ✓", sections,
        tip: "Due-date reminders, settle summaries, and assistant emails all use this layout.");
    return Results.Json(new { sent = ok, error });
});

app.MapDelete("/api/txns/{id:long}", (long id) =>
{
    using var con = Db.Open();
    con.Execute("DELETE FROM txns WHERE id = @id AND settled_id IS NULL", new { id });
    return Results.Ok();
});

// ---------- receipts (photo per transaction) ----------
app.MapPost("/api/txns/{id:long}/receipt", async (long id, HttpRequest req) =>
{
    if (!req.HasFormContentType) return Results.BadRequest();
    var form = await req.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file == null) return Results.BadRequest();
    string ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".pdf" }.Contains(ext))
        return Results.BadRequest("images or pdf only");

    string dir = Path.Combine(dataDir, "receipts");
    Directory.CreateDirectory(dir);
    string name = $"{id}{ext}";
    await using (var fs = File.Create(Path.Combine(dir, name)))
        await file.OpenReadStream().CopyToAsync(fs);
    using var con = Db.Open();
    con.Execute("UPDATE txns SET receipt = @name WHERE id = @id", new { name, id });
    return Results.Json(new { receipt = name });
});

app.MapGet("/api/receipts/{name}", (string name) =>
{
    if (name.Contains("..") || name.Contains('/') || name.Contains('\\')) return Results.BadRequest();
    string path = Path.Combine(dataDir, "receipts", name);
    if (!File.Exists(path)) return Results.NotFound();
    string ct = Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif",
        ".pdf" => "application/pdf", _ => "image/jpeg",
    };
    return Results.File(path, ct);
});

// A leaked payment row (a bank wording the filter didn't know) gets moved out of the
// split ledger into the card's payment memory — same place real payments live.
app.MapPost("/api/txns/{id:long}/make-payment", (long id) =>
{
    using var con = Db.Open();
    var row = con.QueryFirstOrDefault<(long cardId, string? date, string desc, double amount)?>("""
        SELECT card_id, COALESCE(txn_date, post_date), description, amount
        FROM txns WHERE id = @id AND settled_id IS NULL
        """, new { id });
    if (row == null) return Results.NotFound();

    var existing = GetMeta($"detected_pay:{row.Value.cardId}");
    var list = string.IsNullOrEmpty(existing)
        ? new List<PaymentSeen>()
        : JsonSerializer.Deserialize<List<PaymentSeen>>(existing,
              new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true }) ?? new();
    list.Insert(0, new PaymentSeen(row.Value.date, Math.Round(Math.Abs(row.Value.amount), 2), row.Value.desc));
    SetMeta($"detected_pay:{row.Value.cardId}",
        JsonSerializer.Serialize(list, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    con.Execute("DELETE FROM txns WHERE id = @id", new { id });
    return Results.Ok();
});

// Undo the most recent settle-up: charges reopen, the settlement record disappears.
// (Carryovers set during that settle are NOT auto-restored — recheck them.)
app.MapDelete("/api/settlements/{id:long}", (long id) =>
{
    using var con = Db.Open();
    using var tx = con.BeginTransaction();
    con.Execute("UPDATE txns SET settled_id = NULL WHERE settled_id = @id", new { id }, tx);
    con.Execute("DELETE FROM settlements WHERE id = @id", new { id }, tx);
    tx.Commit();
    return Results.Ok();
});

// ---------- import ----------
app.MapPost("/api/cards/{id:long}/import", async (long id, HttpRequest req) =>
{
    if (!req.HasFormContentType) return Results.BadRequest("multipart form expected");
    var form = await req.ReadFormAsync();
    var results = new List<object>();
    foreach (var file in form.Files)
    {
        using var ms = new MemoryStream();
        await using (var s0 = file.OpenReadStream()) await s0.CopyToAsync(ms);
        ms.Position = 0;
        var r = Importer.Import(id, file.FileName, ms);
        LearnFileHint(id, file.FileName);          // next time this bank's file is recognized
        LearnHeaderSig(id, PeekText(file.FileName, ms.ToArray()));
        StoreDetectedPayments(id, r.Payments);
        results.Add(new { file = file.FileName, card = CardName(id), r.Added, r.Duplicates,
                          r.NeedsReview, r.SkippedPayments, r.Warnings, r.Payments, r.MatchedPending });
    }
    return Results.Json(results);
});

// Global import: figure out which card each file belongs to from learned filename hints.
app.MapPost("/api/import-auto", async (HttpRequest req) =>
{
    if (!req.HasFormContentType) return Results.BadRequest("multipart form expected");
    var form = await req.ReadFormAsync();
    var results = new List<object>();
    foreach (var file in form.Files)
    {
        using var ms = new MemoryStream();
        await using (var s0 = file.OpenReadStream()) await s0.CopyToAsync(ms);
        long? cardId = MatchCard(file.FileName, () => PeekText(file.FileName, ms.ToArray()));
        if (cardId == null)
        {
            results.Add(new { file = file.FileName, card = (string?)null,
                              error = "couldn't tell which card this is — set the card's last-4 digits (next to its name), or import it on the card once" });
            continue;
        }
        ms.Position = 0;
        var r = Importer.Import(cardId.Value, file.FileName, ms);
        StoreDetectedPayments(cardId.Value, r.Payments);
        results.Add(new { file = file.FileName, card = CardName(cardId.Value), r.Added,
                          r.Duplicates, r.NeedsReview, r.SkippedPayments, r.Warnings, r.Payments, r.MatchedPending });
    }
    return Results.Json(results);
});

// Scan the statements folder (where downloads get dropped) and import whatever it recognizes.
app.MapPost("/api/scan", () =>
{
    string folder = GetMeta("statements_folder") ?? @"C:\Users\Rando\Claude Play";
    if (!Directory.Exists(folder)) return Results.Json(new[] { new { file = folder, card = (string?)null, error = "folder not found" } });

    var results = new List<object>();
    bool archive = GetMeta("archive_after_scan") == "1";
    string naming = GetMeta("archive_naming") ?? "datecard";

    // Recurse into subfolders — but NEVER into our own Archive tree, or we'd re-scan and
    // re-archive files we already filed away (an endless rename loop).
    var all = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .Where(f => (f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) &&
                    !Path.GetFileName(f).StartsWith("._") && !Path.GetFileName(f).StartsWith("~") &&
                    !f.Replace('/', '\\').Contains(@"\Archive\", StringComparison.OrdinalIgnoreCase)).ToList();

    string Rel(string p) => Path.GetRelativePath(folder, p);   // show subfolder context

    // Flag any SplitStatement export (a save-point) found anywhere in the tree — don't import it.
    foreach (var path in all.Where(IsSavePoint))
        results.Add(new { file = Rel(path), card = (string?)null, savePoint = true,
                          error = "looks like a SplitStatement save-point — use Settings → Restore to load it" });

    var files = all.Where(f => !IsSavePoint(f) &&
        !Path.GetFileName(f).StartsWith("Money Split", StringComparison.OrdinalIgnoreCase));
    foreach (var path in files)
    {
        string name = Path.GetFileName(path), rel = Rel(path);
        try
        {
            // Share-tolerant read: the file may be open in Excel on someone's machine.
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                           FileShare.ReadWrite | FileShare.Delete))
            using (var buf = new MemoryStream())
            {
                fs.CopyTo(buf);
                bytes = buf.ToArray();
            }

            long? cardId = MatchCard(name, () => PeekText(name, bytes));
            if (cardId == null)
            {
                results.Add(new { file = rel, card = (string?)null,
                                  error = "couldn't tell which card — set the card's last-4 digits (next to its name), or import it on the card once" });
                continue;
            }
            using (var s = new MemoryStream(bytes))
            {
                var r = Importer.Import(cardId.Value, name, s);
                StoreDetectedPayments(cardId.Value, r.Payments);
                string? archivedTo = archive ? ArchiveFile(path, folder, CardName(cardId.Value), naming) : null;
                results.Add(new { file = rel, card = CardName(cardId.Value), r.Added, r.Duplicates,
                                  r.NeedsReview, r.SkippedPayments, r.Warnings, r.Payments, r.MatchedPending,
                                  archivedTo });
            }
        }
        catch (IOException ex)
        {
            // One locked or unreadable file must not sink the whole scan.
            results.Add(new { file = rel, card = (string?)null,
                              error = $"couldn't read it ({ex.Message.TrimEnd('.')}) — close it and rescan" });
        }
    }
    if (results.Count == 0)
        results.Add(new { file = folder, card = (string?)null, error = "no statement files found here" });
    return Results.Json(results);
});

// Paste of Amazon's "Transactions" page: match card-funded orders to open charges by
// amount + date and toggle them to the person who pasted. Gift-card orders never hit
// the card statement, so they're skipped.
app.MapPost("/api/cards/{id:long}/amazon-match", async (long id, HttpRequest req) =>
{
    var b = await req.ReadFromJsonAsync<AmazonBody>();
    if (b == null || string.IsNullOrWhiteSpace(b.Text) ||
        (b.Who != "Aryn" && b.Who != "Mel" && b.Who != "Shared"))
        return Results.BadRequest();

    var dateRx = new System.Text.RegularExpressions.Regex(
        @"^(January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},\s+\d{4}$");
    var amtRx = new System.Text.RegularExpressions.Regex(@"^(?<method>.*?)(?<sign>[-+−])\$(?<amt>[\d,]+\.\d{2})$");
    var orderRx = new System.Text.RegularExpressions.Regex(@"Order\s*#\s*([A-Z0-9\-]+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    using var con = Db.Open();
    string? last4 = con.QueryFirstOrDefault<string>("SELECT last4 FROM cards WHERE id = @id", new { id });
    var cardDigits = CardDigits(last4).ToList();

    var lines = b.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    DateTime? current = null;
    int matched = 0, giftCard = 0;
    var unmatched = new List<string>();
    var usedTxns = new HashSet<long>();

    for (int i = 0; i < lines.Count; i++)
    {
        if (dateRx.IsMatch(lines[i]))
        {
            if (DateTime.TryParse(lines[i], System.Globalization.CultureInfo.InvariantCulture,
                                  System.Globalization.DateTimeStyles.None, out var d))
                current = d;
            continue;
        }
        var m = amtRx.Match(lines[i]);
        if (!m.Success || current == null) continue;

        string method = m.Groups["method"].Value.Trim();
        double amt = double.Parse(m.Groups["amt"].Value.Replace(",", ""),
                                  System.Globalization.CultureInfo.InvariantCulture);
        bool refund = m.Groups["sign"].Value != "-";
        double target = refund ? -amt : amt;

        // order # + merchant usually follow on the next lines
        string? order = null, merchant = null;
        for (int j = i + 1; j < Math.Min(i + 3, lines.Count); j++)
        {
            var om = orderRx.Match(lines[j]);
            if (om.Success) { order = om.Groups[1].Value; continue; }
            if (!dateRx.IsMatch(lines[j]) && !amtRx.IsMatch(lines[j])) { merchant = lines[j]; break; }
        }

        bool onThisCard = method.Contains("visa", StringComparison.OrdinalIgnoreCase) ||
                          cardDigits.Any(d => method.Contains(d));
        if (!onThisCard) { giftCard++; continue; }

        var hit = con.QueryFirstOrDefault<(long txnId, string? note)?>("""
            SELECT id, note FROM txns
            WHERE card_id = @id AND settled_id IS NULL AND ABS(amount - @target) < 0.005
              AND bucket != 'Skip'
              AND ABS(julianday(COALESCE(txn_date, post_date)) - julianday(@d)) <= 5
              AND id NOT IN @used
            ORDER BY ABS(julianday(COALESCE(txn_date, post_date)) - julianday(@d))
            LIMIT 1
            """, new { id, target, d = current.Value.ToString("yyyy-MM-dd"),
                       used = usedTxns.Count > 0 ? usedTxns.ToArray() : new long[] { 0 } });
        if (hit == null)
        {
            unmatched.Add($"{current:M/d} {(refund ? "+" : "")}${amt:0.00} ({merchant ?? "?"})");
            continue;
        }

        usedTxns.Add(hit.Value.txnId);
        string? newNote = string.IsNullOrWhiteSpace(hit.Value.note)
            ? (merchant ?? "Amazon") + (order != null ? $" #…{order[^Math.Min(7, order.Length)..]}" : "")
            : null;
        con.Execute("""
            UPDATE txns SET bucket = @who, needs_review = 0,
                            note = COALESCE(@newNote, note)
            WHERE id = @txnId
            """, new { who = b.Who, newNote, hit.Value.txnId });
        var desc = con.QueryFirstOrDefault<string>("SELECT description FROM txns WHERE id = @txnId",
                                                   new { hit.Value.txnId });
        if (desc != null) Importer.LearnRule(con, Importer.NormalizeMerchant(desc), b.Who);
        matched++;
    }

    return Results.Json(new { matched, giftCard, unmatched });
});

app.MapGet("/api/cards/{id:long}/payments-detected", (long id) =>
    Results.Content(GetMeta($"detected_pay:{id}") ?? "[]", "application/json"));

app.MapPost("/api/cards/{id:long}/auto-balance", (long id) =>
{
    var json = GetMeta($"detected_pay:{id}");
    if (string.IsNullOrEmpty(json)) return Results.NotFound();
    var payments = JsonSerializer.Deserialize<List<PaymentSeen>>(json,
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });
    if (payments == null || payments.Count == 0) return Results.NotFound();

    var best = payments.OrderByDescending(p => p.Date).First();
    using var con = Db.Open();
    con.Execute("""
        UPDATE cards SET stmt_balance = @bal,
                         stmt_balance_at = datetime('now')
        WHERE id = @id
        """, new { id, bal = best.Amount });
    return Results.Ok(new { balance = best.Amount, date = best.Date });
});

app.MapPost("/api/cards/{id:long}/bulk-settle-before", (long id, HttpRequest req) =>
{
    string? date = req.Query["date"];
    if (string.IsNullOrEmpty(date)) return Results.BadRequest();
    
    using var con = Db.Open();
    using var tx = con.BeginTransaction();
    
    // Create a 'catch-up' settlement for all old charges
    long sid = con.ExecuteScalar<long>("""
        INSERT INTO settlements (card_id, label, shared_total, mel_total, aryn_total, note, by)
        SELECT @id, 'Catch-up', 
               SUM(CASE WHEN bucket='Shared' THEN amount ELSE 0 END),
               SUM(CASE WHEN bucket='Mel' THEN amount ELSE 0 END),
               SUM(CASE WHEN bucket='Aryn' THEN amount ELSE 0 END),
               'Automated catch-up for charges before ' || @date, 'System'
        FROM txns WHERE card_id = @id AND settled_id IS NULL AND (txn_date < @date OR post_date < @date);
        SELECT last_insert_rowid();
        """, new { id, date }, tx);
        
    con.Execute("""
        UPDATE txns SET settled_id = @sid 
        WHERE card_id = @id AND settled_id IS NULL AND (txn_date < @date OR post_date < @date)
        """, new { sid, id, date }, tx);
        
    tx.Commit();
    return Results.Ok(new { settlementId = sid });
});

// ---------- cards ----------
app.MapPost("/api/cards", async (HttpRequest req) =>
{
    var b = await req.ReadFromJsonAsync<CardPatch>();
    if (string.IsNullOrWhiteSpace(b?.Name)) return Results.BadRequest();
    // Auto-classify the new card from its name so it arrives looking right with rewards filled.
    var t = CardCatalog.Detect(b.Name);
    using var con = Db.Open();
    con.Execute("""
        INSERT INTO cards (name, due_day, color, default_bucket, default_payer, note, card_type, rewards)
        VALUES (@Name, @DueDay, @color, COALESCE(@DefaultBucket, 'Shared'),
                COALESCE(@DefaultPayer, 'Aryn'), @note, @ctype, @rewards)
        """, new { b.Name, b.DueDay, b.DefaultBucket, b.DefaultPayer,
                   note = t != null ? CardCatalog.ApplyTags(b.Note, t.Key) : b.Note,
                   color = t != null ? $"skin:{t.Skin}" : "auto",
                   ctype = t?.Key, rewards = t?.Rewards });
    return Results.Ok();
});

// The built-in card knowledge base — drives the card-type picker.
app.MapGet("/api/card-catalog", () =>
    Results.Json(CardCatalog.All.Select(c => new { c.Key, c.Name, c.Skin, c.Rewards })));

app.MapPatch("/api/cards/{id:long}", async (long id, HttpRequest req) =>
{
    var b = await req.ReadFromJsonAsync<CardPatch>() ?? new CardPatch();
    using var con = Db.Open();
    // Picking a card type applies its skin + rewards + reward chips (unless overridden).
    string? color = b.Color, rewards = null, note = b.Note;
    if (!string.IsNullOrWhiteSpace(b.CardType))
    {
        var t = CardCatalog.ByKey(b.CardType);
        rewards = t.Rewards;
        if (b.Color == null && t.Key != "other") color = $"skin:{t.Skin}";
        // merge reward chips into the (possibly existing) note
        string? baseNote = note ?? con.QueryFirstOrDefault<string>(
            "SELECT note FROM cards WHERE id = @id", new { id });
        note = CardCatalog.ApplyTags(baseNote, t.Key);
    }
    con.Execute("""
        UPDATE cards SET
            name = COALESCE(@Name, name),
            due_day = COALESCE(@DueDay, due_day),
            color = COALESCE(@color, color),
            default_bucket = COALESCE(@DefaultBucket, default_bucket),
            default_payer = COALESCE(@DefaultPayer, default_payer),
            carry_mel = COALESCE(@CarryMel, carry_mel),
            carry_aryn = COALESCE(@CarryAryn, carry_aryn),
            note = COALESCE(@note, note),
            archived = COALESCE(@Archived, archived),
            last4 = COALESCE(@Last4, last4),
            card_type = COALESCE(@CardType, card_type),
            rewards = COALESCE(@rewards, rewards)
        WHERE id = @id
        """, new { id, b.Name, b.DueDay, color, b.DefaultBucket, b.DefaultPayer,
                   b.CarryMel, b.CarryAryn, note, b.Archived, b.Last4, b.CardType, rewards });
    return Results.Ok();
});

app.MapPost("/api/cards/{id:long}/balance", async (long id, HttpRequest req) =>
{
    var b = await req.ReadFromJsonAsync<BalanceBody>();
    using var con = Db.Open();
    con.Execute("""
        UPDATE cards SET stmt_balance = @bal,
                         stmt_balance_at = CASE WHEN @bal IS NULL THEN NULL ELSE datetime('now') END
        WHERE id = @id
        """, new { id, bal = b?.Balance });
    return Results.Ok();
});

// ---------- settle ----------
app.MapPost("/api/cards/{id:long}/settle", async (long id, HttpRequest req) =>
{
    var b = await req.ReadFromJsonAsync<SettleBody>() ?? new SettleBody();
    using var con = Db.Open();
    var card = con.QueryFirst<(double carryMel, double carryAryn)>(
        "SELECT carry_mel, carry_aryn FROM cards WHERE id = @id", new { id });
    var t = con.QueryFirst<(double shared, double mel, double aryn)>("""
        SELECT COALESCE(SUM(CASE WHEN bucket='Shared' THEN amount END), 0),
               COALESCE(SUM(CASE WHEN bucket='Mel'    THEN amount END), 0),
               COALESCE(SUM(CASE WHEN bucket='Aryn'   THEN amount END), 0)
        FROM txns WHERE card_id = @id AND settled_id IS NULL
        """, new { id });

    double melPart = Math.Round(t.shared / 2 + t.mel + card.carryMel, 2);
    double arynPart = Math.Round(t.shared / 2 + t.aryn + card.carryAryn, 2);

    using var tx = con.BeginTransaction();
    long sid = con.ExecuteScalar<long>("""
        INSERT INTO settlements (card_id, label, shared_total, mel_total, aryn_total,
                                 mel_part, aryn_part, payments, note, by)
        VALUES (@id, @Label, @shared, @mel, @aryn, @melPart, @arynPart, @Payments, @Note, @By);
        SELECT last_insert_rowid();
        """, new { id, b.Label, t.shared, t.mel, t.aryn, melPart, arynPart,
                   b.Payments, b.Note, b.By }, tx);
    con.Execute("UPDATE txns SET settled_id = @sid WHERE card_id = @id AND settled_id IS NULL",
                new { sid, id }, tx);
    con.Execute("""
        UPDATE cards SET carry_mel = @cm, carry_aryn = @ca, stmt_balance = NULL, stmt_balance_at = NULL
        WHERE id = @id
        """, new { id, cm = b.NewCarryMel ?? 0, ca = b.NewCarryAryn ?? 0 }, tx);
    tx.Commit();

    // Auto-export the cycle to Excel on the share — the old source-of-truth habit, automatic.
    try
    {
        string folder = Path.Combine(GetMeta("statements_folder") ?? dataDir, "exports");
        ExcelExport.ExportCycle(sid, folder);
    }
    catch { /* export is a bonus, never a blocker */ }

    return Results.Json(new { settlementId = sid, melPart, arynPart });
});

// One-click backup: datestamped copy of the database next to the statements.
app.MapPost("/api/backup", () =>
{
    string folder = Path.Combine(GetMeta("statements_folder") ?? dataDir, "backups");
    Directory.CreateDirectory(folder);
    string dest = Path.Combine(folder, $"moneysplit-{DateTime.Now:yyyy-MM-dd-HHmm}.db");
    File.Copy(Db.Path, dest, overwrite: true);
    return Results.Json(new { saved = dest });
});

// Every unconfirmed "venmo requested" across all settle-ups — the audit list.
app.MapGet("/api/requests-outstanding", () =>
{
    using var con = Db.Open();
    var result = new List<object>();
    var rows = con.Query<(long id, long? cardId, string? label, string settledAt, string? payments)>("""
        SELECT s.id, s.card_id, s.label, s.settled_at, s.payments
        FROM settlements s WHERE s.payments LIKE '%requested%' ORDER BY s.settled_at DESC
        """);
    foreach (var s in rows)
    {
        List<Dictionary<string, JsonElement>> pays;
        try { pays = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(s.payments ?? "[]") ?? new(); }
        catch { continue; }
        for (int i = 0; i < pays.Count; i++)
        {
            if (!pays[i].TryGetValue("method", out var m) || !m.GetString()!.Contains("requested")) continue;
            result.Add(new
            {
                settlementId = s.id,
                index = i,
                card = s.cardId == null ? "?" : CardName(s.cardId.Value),
                label = s.label,
                settledAt = s.settledAt[..Math.Min(10, s.settledAt.Length)],
                who = pays[i].TryGetValue("who", out var w) ? w.GetString() : "?",
                amount = pays[i].TryGetValue("amount", out var a) ? a.GetDouble() : 0,
                daysOld = (int)(DateTime.Now - DateTime.Parse(s.settledAt)).TotalDays,
            });
        }
    }
    return Results.Json(result);
});

// A "venmo requested" line becomes "venmo received" once the money actually lands.
app.MapPost("/api/settlements/{id:long}/confirm-payment", async (long id, HttpRequest req) =>
{
    var b = await req.ReadFromJsonAsync<ConfirmBody>();
    using var con = Db.Open();
    var json = con.QueryFirstOrDefault<string>(
        "SELECT payments FROM settlements WHERE id = @id", new { id });
    if (json == null) return Results.NotFound();
    var list = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json) ?? new();
    if (b == null || b.Index < 0 || b.Index >= list.Count) return Results.BadRequest();
    var entry = list[b.Index].ToDictionary(kv => kv.Key, kv => (object)kv.Value);
    entry["method"] = "venmo received";
    var rebuilt = list.Select((d, i) => i == b.Index
        ? entry
        : d.ToDictionary(kv => kv.Key, kv => (object)kv.Value)).ToList();
    con.Execute("UPDATE settlements SET payments = @p WHERE id = @id",
                new { p = JsonSerializer.Serialize(rebuilt), id });
    return Results.Ok();
});

app.MapGet("/api/cards/{id:long}/settlements", (long id) =>
{
    using var con = Db.Open();
    var rows = con.Query("""
        SELECT id, settled_at AS settledAt, label, shared_total AS sharedTotal,
               mel_total AS melTotal, aryn_total AS arynTotal,
               mel_part AS melPart, aryn_part AS arynPart, payments, note, by
        FROM settlements WHERE card_id = @id ORDER BY settled_at DESC
        """, new { id });
    return Results.Json(rows);
});

// Live model discovery: ask the provider which models THIS key can call.
app.MapGet("/api/ai/models", async () =>
{
    string provider = GetMeta("ai_provider") ?? "anthropic";
    string? key = GetMeta($"ai_key_{provider}");
    if (string.IsNullOrEmpty(key)) return Results.Json(new { available = Array.Empty<string>(), error = "no key" });
    try
    {
        var ids = await AiAgent.ListModels(provider, key);
        return Results.Json(new { available = ids });
    }
    catch (Exception ex)
    {
        return Results.Json(new { available = Array.Empty<string>(), error = ex.Message });
    }
});

// ---------- AI assistant (optional — needs an API key in Settings) ----------
app.MapPost("/api/ai/chat", async (HttpRequest req) =>
{
    var b = await req.ReadFromJsonAsync<AiChatBody>();
    string provider = GetMeta("ai_provider") ?? "anthropic";
    string? key = GetMeta($"ai_key_{provider}");
    if (string.IsNullOrEmpty(key))
        return Results.Json(new { reply = $"No {provider} API key yet — add it in ⚙ Settings and I come alive." });
    string? model = GetMeta("ai_model");
    if (string.IsNullOrWhiteSpace(model))
        model = provider == "google" ? "gemini-flash-latest" : "claude-sonnet-4-6";   // auto = best that Just Works
    int maxSteps = int.TryParse(GetMeta("ai_max_steps"), out var ms) ? Math.Clamp(ms, 6, 80) : 16;
    string reply = await AiAgent.Chat(b?.Messages ?? new(), provider, key, model,
        GetMeta("person_a") ?? "Aryn", GetMeta("person_b") ?? "Mel", maxSteps);
    return Results.Json(new { reply });
});

// Restore a save-point: rebuild all data from a SplitStatement Excel export.
app.MapPost("/api/restore", async (HttpRequest req) =>
{
    if (!req.HasFormContentType) return Results.BadRequest("upload the Excel file");
    var file = (await req.ReadFormAsync()).Files.FirstOrDefault();
    if (file == null) return Results.BadRequest("no file");
    try
    {
        using var s = file.OpenReadStream();
        var (cards, txns, settlements) = BackupService.Restore(s);
        return Results.Json(new { restored = true, cards, txns, settlements });
    }
    catch (Exception ex) { return Results.Json(new { restored = false, error = ex.Message }); }
});

// ---------- export ----------
app.MapGet("/api/export.xlsx", () =>
    Results.File(ExcelExport.Build(),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        $"MoneySplit-export-{DateTime.Now:yyyy-MM-dd}.xlsx"));

// ---------- due-date reminder engine ----------
// Checks hourly: any card with open charges whose due date is exactly N days out
// (N from Settings) gets one reminder email per cycle.
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            if (int.TryParse(GetMeta("remind_days"), out int lead) && lead > 0)
            {
                // Progressive ("annoying") mode: lead day, ~halfway, and the due day itself.
                bool annoying = GetMeta("remind_progressive") == "1";
                var stages = annoying
                    ? new[] { lead, Math.Max(1, lead / 2), 0 }.Distinct().ToArray()
                    : new[] { lead };

                using var con = Db.Open();
                var cards = con.Query<(long id, string name, long? dueDay)>(
                    "SELECT id, name, due_day FROM cards WHERE archived = 0 AND due_day IS NOT NULL");
                foreach (var c in cards)
                {
                    int? dueIn = DueInDays(c.dueDay);
                    if (dueIn == null || !stages.Contains(dueIn.Value)) continue;
                    var t = con.QueryFirst<(double shared, double mel, double aryn)>("""
                        SELECT COALESCE(SUM(CASE WHEN bucket='Shared' THEN amount END), 0),
                               COALESCE(SUM(CASE WHEN bucket='Mel'    THEN amount END), 0),
                               COALESCE(SUM(CASE WHEN bucket='Aryn'   THEN amount END), 0)
                        FROM txns WHERE card_id = @id AND settled_id IS NULL
                        """, new { id = c.id });
                    double open = Math.Round(t.shared + t.mel + t.aryn, 2);
                    if (open <= 0.005) continue;   // settled / nothing owed — no nag

                    string cycleKey = $"reminded:{c.id}:{DateTime.Today.AddDays(dueIn.Value):yyyy-MM}:{dueIn.Value}";
                    if (GetMeta(cycleKey) != null) continue;

                    string pA = GetMeta("person_a") ?? "Aryn", pB = GetMeta("person_b") ?? "Mel";
                    string subject = dueIn.Value switch
                    {
                        0 => $"🚨 {c.name} is DUE TODAY — ${open:0.00} still not settled!",
                        1 => $"⏰ {c.name} due TOMORROW — ${open:0.00} not settled",
                        _ when dueIn.Value < lead => $"⏰ {c.name} due in {dueIn.Value} days — still ${open:0.00} open",
                        _ => $"💳 {c.name} due in {dueIn.Value} day{(dueIn.Value == 1 ? "" : "s")} — ${open:0.00} not settled",
                    };
                    string sections = "";
                    if (dueIn.Value == 0)
                        sections += Mailer.AlertBubble("Last call",
                            $"<b>{c.name}</b> is due <b>today</b> — settle it now or the late-fee goblin wins.");
                    else if (dueIn.Value < lead)
                        sections += Mailer.AlertBubble("Follow-up",
                            $"Friendly-ish nudge — <b>{c.name}</b> is still sitting there, due in <b>{dueIn.Value} day{(dueIn.Value == 1 ? "" : "s")}</b>.");
                    sections += Mailer.HeroBubble($"{c.name} · due on the {c.dueDay}",
                        $"<span style='font-size:30px;font-weight:700;color:#0b57d0;letter-spacing:-0.5px;'>${open:0.00}</span><br><span style='font-size:13.5px;'>still open and unsettled</span>");
                    sections += Mailer.Bubble("The split",
                        Mailer.Row("Shared", $"${t.shared:0.00}") +
                        Mailer.Row("→ each", $"${t.shared / 2:0.00}") +
                        Mailer.Row($"{pB} only", $"${t.mel:0.00}") +
                        Mailer.Row($"{pA} only", $"${t.aryn:0.00}"));
                    var (ok, _) = Mailer.SendHtml(subject, sections,
                        tip: "Open SplitStatement, give the review flags a quick pass, then hit ✅ Settle up — the venmo request line is pre-filled.");
                    if (ok) SetMeta(cycleKey, DateTime.Now.ToString("s"));
                }
            }
        }
        catch { /* reminders must never crash the app */ }
        await Task.Delay(TimeSpan.FromHours(1));
    }
});

// ---------- go ----------
// Advertise "split.local" on the home network via mDNS, so phones and laptops can reach
// the app by name while it's running — no router config or hosts-file edits needed.
MulticastService? mdns = null;
try
{
    mdns = new MulticastService();

    // IPv4 A records ONLY. This machine has just link-local/ULA IPv6 — advertising those
    // sends phones down six dead ends before they try IPv4. AAAA questions get an NSEC
    // ("no IPv6 here") so askers skip the wait — but ONLY when asked: Windows' resolver
    // discards responses that volunteer unexpected DNSSEC records.
    void AddSplitRecords(List<ResourceRecord> answers)
    {
        foreach (var ip in MulticastService.GetIPAddresses()
                     .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)))
            answers.Add(new ARecord { Name = "split.local", Address = ip, TTL = TimeSpan.FromMinutes(2) });
    }

    mdns.QueryReceived += (s, e) =>
    {
        try
        {
            if (!e.Message.Questions.Any(q =>
                    q.Name.ToString().Equals("split.local", StringComparison.OrdinalIgnoreCase)))
                return;
            var res = e.Message.CreateResponse();
            AddSplitRecords(res.Answers);
            if (res.Answers.Count > 0) mdns!.SendAnswer(res);
        }
        catch { /* a bad packet must never silence the responder */ }
    };
    mdns.Start();

    // Keep every device's mDNS cache warm with unsolicited announcements — resolution
    // becomes a cache hit instead of a multicast query round-trip (the load-time lag).
    _ = Task.Run(async () =>
    {
        while (true)
        {
            try
            {
                var msg = new Message { QR = true };
                AddSplitRecords(msg.Answers);
                if (msg.Answers.Count > 0) mdns!.SendAnswer(msg);
            }
            catch { /* network blips are fine */ }
            await Task.Delay(TimeSpan.FromSeconds(90));   // re-announce inside the 2-min TTL
        }
    });
    Console.WriteLine("    Friendly name: http://split.local   (any device on the wifi)");
}
catch (Exception ex)
{
    Console.WriteLine($"    (split.local alias unavailable: {ex.Message})");
}
app.Lifetime.ApplicationStopping.Register(() => mdns?.Dispose());

PrintAddresses();
app.Run();

static int? DueInDays(long? dueDay)
{
    if (dueDay is not long dl || dl < 1 || dl > 31) return null;
    int d = (int)dl;
    var today = DateTime.Today;
    var due = new DateTime(today.Year, today.Month, Math.Min(d, DateTime.DaysInMonth(today.Year, today.Month)));
    if (due < today)
    {
        var next = today.AddMonths(1);
        due = new DateTime(next.Year, next.Month, Math.Min(d, DateTime.DaysInMonth(next.Year, next.Month)));
    }
    return (due - today).Days;
}

static void PrintAddresses()
{
    Console.WriteLine();
    Console.WriteLine("  Money Split is running:");
    Console.WriteLine("    On this PC:   http://localhost:5275  (or http://split/ if the hosts alias is set)");
    try
    {
        var ips = Dns.GetHostAddresses(Dns.GetHostName())
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        foreach (var ip in ips)
            Console.WriteLine($"    Mel's phone:  http://{ip}:5275   (same wifi)");
    }
    catch { /* fine, localhost still works */ }
    Console.WriteLine();
}

static string? GetMeta(string k)
{
    using var con = Db.Open();
    return con.QueryFirstOrDefault<string>("SELECT v FROM meta WHERE k = @k", new { k });
}

static void SetMeta(string k, string v)
{
    using var con = Db.Open();
    con.Execute("INSERT INTO meta (k, v) VALUES (@k, @v) ON CONFLICT(k) DO UPDATE SET v = @v", new { k, v });
}

/// <summary>Is this file a SplitStatement export/save-point (not a bank statement)?</summary>
static bool IsSavePoint(string path)
{
    string n = Path.GetFileName(path).ToLowerInvariant();
    if (n.Contains("moneysplit") || n.Contains("splitstatement") || n.Contains("-export-")) return true;
    if (!n.EndsWith(".xlsx")) return false;
    try
    {
        using var wb = new ClosedXML.Excel.XLWorkbook(path);   // a save-point carries the hidden _cards sheet
        return wb.Worksheets.Contains("_cards");
    }
    catch { return false; }
}

/// <summary>Move an imported statement into Archive\year\month with the chosen naming. Returns the new path.</summary>
static string? ArchiveFile(string path, string folder, string cardName, string naming)
{
    try
    {
        var now = DateTime.Now;
        string dir = Path.Combine(folder, "Archive", now.ToString("yyyy"), now.ToString("MM-MMMM"));
        Directory.CreateDirectory(dir);
        string ext = Path.GetExtension(path);
        string orig = Path.GetFileNameWithoutExtension(path);
        string date = now.ToString("yyyy-MM-dd");
        string card = string.Join("_", cardName.Split(Path.GetInvalidFileNameChars()));
        string baseName = naming switch
        {
            "date"            => $"{date}_{orig}",
            "card"            => $"{card}_{orig}",
            "datecard"        => $"{date}_{card}_{orig}",
            "datecard_replace" => $"{date}_{card}",
            _                  => orig,   // "keep"
        };
        string dest = Path.Combine(dir, baseName + ext);
        for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, $"{baseName} ({i}){ext}");
        File.Move(path, dest);
        return dest;
    }
    catch { return null; }   // archiving is a convenience, never a blocker
}

/// <summary>First few KB of a CSV as text for card matching; binary formats return null.</summary>
static string? PeekText(string fileName, byte[] bytes)
{
    if (!fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return null;
    string text = System.Text.Encoding.UTF8.GetString(bytes);
    return text.Length > 6000 ? text[..6000] : text;
}

static string CardName(long id)
{
    using var con = Db.Open();
    return con.QueryFirstOrDefault<string>("SELECT name FROM cards WHERE id = @id", new { id }) ?? "Unknown";
}

static void LearnFileHint(long id, string name)
{
    // Try to find a good prefix (e.g. "Chase9425" from "Chase9425_Activity2026.csv")
    string hint = name;
    if (name.Contains('_')) hint = name.Split('_')[0];
    else if (name.Contains(' ')) hint = name.Split(' ')[0];
    if (hint.Contains('.')) hint = hint.Split('.')[0];

    // Generic bank-export names ("activity.csv", "transactions.csv") fit EVERY card —
    // learning them would misroute other cards' files. Same for date-shaped names like
    // "2026-06-11" or "May2026", which match OTHER cards' files from the same day/month.
    // Skip those; last-4 matching covers them. A good hint starts with 4+ letters of a
    // real bank name ("Chase9425") that isn't a generic word or a month.
    string[] generic = { "activity", "transactions", "transaction", "export", "statement",
                         "statements", "download", "data", "history", "summary",
                         "january", "february", "march", "april", "may", "june", "july",
                         "august", "september", "october", "november", "december" };
    string letters = new string(hint.TakeWhile(char.IsLetter).ToArray());
    if (letters.Length < 4 || generic.Contains(letters.ToLowerInvariant()) ||
        generic.Contains(hint.ToLowerInvariant())) return;

    using var con = Db.Open();
    con.Execute("UPDATE cards SET file_hint = @hint WHERE id = @id", new { id, hint });
}

/// <summary>Match a statement file to a card: filename hint → last-4 in the filename →
/// last-4 inside the file content (Cap1/Amex exports carry a card-number column).</summary>
static long? MatchCard(string name, Func<string?> contentPeek)
{
    using var con = Db.Open();
    var byHint = con.QueryFirstOrDefault<long?>("""
        SELECT id FROM cards
        WHERE file_hint IS NOT NULL AND file_hint != '' AND @name LIKE '%' || file_hint || '%'
        """, new { name });
    if (byHint != null) return byHint;

    // A card may have several numbers (one per cardholder) — match any of them.
    var cards = con.Query<(long id, string? last4)>(
        "SELECT id, last4 FROM cards WHERE last4 IS NOT NULL AND last4 != '' AND archived = 0").ToList();

    foreach (var c in cards)
        foreach (var digits in CardDigits(c.last4))
            if (name.Contains(digits, StringComparison.Ordinal)) return c.id;

    string? text = contentPeek();
    if (text != null)
        foreach (var c in cards)
            foreach (var digits in CardDigits(c.last4))
            {
                // Standalone cell ("…,7709,…") or a masked token ending in the digits ("XXXX-1005").
                var rx = new System.Text.RegularExpressions.Regex(
                    $@"(^|[,""\s])[Xx\*\-•#]*{digits}($|[,""\s])");
                if (rx.IsMatch(text)) return c.id;
            }

    // Last resort: header fingerprint. Some banks (AAA) export a generic "transactions.csv"
    // with no card number inside — but their column layout is one of a kind. If exactly one
    // card has imported a file with this exact header before, that's our card.
    string? sig = HeaderSig(text);
    if (sig != null)
    {
        var hits = con.Query<string>(
            "SELECT k FROM meta WHERE k LIKE 'header_sig:%' AND v = @sig", new { sig }).ToList();
        if (hits.Count == 1 && long.TryParse(hits[0].Split(':')[1], out var hid)) return hid;
    }
    return null;
}

static string? HeaderSig(string? text)
{
    if (text == null) return null;
    var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
    if (line == null || !line.Contains(',')) return null;
    return line.Replace("\"", "").Replace(" ", "").ToLowerInvariant();
}

static void LearnHeaderSig(long id, string? text)
{
    var sig = HeaderSig(text);
    if (sig != null) SetMeta($"header_sig:{id}", sig);
}

static IEnumerable<string> CardDigits(string? last4) =>
    System.Text.RegularExpressions.Regex.Split(last4 ?? "", @"\D+").Where(d => d.Length >= 4);

static void StoreDetectedPayments(long id, List<PaymentSeen> payments)
{
    if (payments == null || payments.Count == 0) return;
    var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    // Accumulate across imports (a narrow monthly export must not erase older memory).
    var existing = GetMeta($"detected_pay:{id}");
    var list = string.IsNullOrEmpty(existing)
        ? new List<PaymentSeen>()
        : JsonSerializer.Deserialize<List<PaymentSeen>>(existing, opts) ?? new();
    foreach (var p in payments)
        if (!list.Any(e => e.Date == p.Date && Math.Abs(e.Amount - p.Amount) < 0.005))
            list.Add(p);
    list = list.OrderByDescending(p => p.Date ?? "").Take(60).ToList();
    SetMeta($"detected_pay:{id}", JsonSerializer.Serialize(list, opts));
}

class CardRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long? DueDay { get; set; }
    public string Color { get; set; } = "#4F86C6";
    public string DefaultBucket { get; set; } = "Shared";
    public string DefaultPayer { get; set; } = "Aryn";
    public double CarryMel { get; set; }
    public double CarryAryn { get; set; }
    public double? StmtBalance { get; set; }
    public string? StmtBalanceAt { get; set; }
    public string? Note { get; set; }
    public string? Last4 { get; set; }
    public string? CardType { get; set; }
    public string? Rewards { get; set; }
}
record TxnPatch(string? Bucket = null, string? Note = null, bool? Confirm = null,
                bool? Flag = null, string? FlagReason = null, string? FlagBy = null);
record ManualTxn(long CardId, string? Date, string Description, double Amount,
                 string Bucket = "Shared", string? Note = null, string Source = "manual");
record SettingsBody(string? StatementsFolder, string? ArynBank = null, string? MelBank = null,
                    string? BlurList = null, string? PersonA = null, string? PersonB = null,
                    string? AiProvider = null, string? AiKey = null, string? AiModel = null,
                    string? AiMaxSteps = null,
                    string? SmtpHost = null, string? SmtpPort = null, string? SmtpUser = null,
                    string? SmtpPass = null, string? EmailTo = null, string? RemindDays = null,
                    bool? RemindProgressive = null, bool? ArchiveAfterScan = null, string? ArchiveNaming = null);
record AmazonBody(string Who, string Text);
record ConfirmBody(int Index);
record AiChatBody(List<ChatMsg> Messages);
record CardPatch(string? Name = null, int? DueDay = null, string? Color = null,
                 string? DefaultBucket = null, string? DefaultPayer = null,
                 double? CarryMel = null, double? CarryAryn = null, string? Note = null,
                 int? Archived = null, string? Last4 = null, string? CardType = null);
record BalanceBody(double? Balance);
record SettleBody(string? Label = null, string? Payments = null, string? Note = null,
                  string? By = null, double? NewCarryMel = null, double? NewCarryAryn = null);
