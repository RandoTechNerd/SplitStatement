using System.Data;
using ClosedXML.Excel;
using Dapper;
using Microsoft.Data.Sqlite;

namespace MoneySplit;

/// <summary>
/// Makes the Excel export double as a save-point. Alongside the human-readable cycle tabs,
/// the export carries hidden raw sheets (_cards, _txns, _settlements, _meta) — a faithful
/// dump of every table. A fresh install can Restore from that file to land in the exact
/// same arrangement. Secrets (API keys, SMTP password) are deliberately left out so a
/// shared file never leaks credentials.
/// </summary>
public static class BackupService
{
    private static readonly string[] SecretMetaKeys =
        { "ai_key_anthropic", "ai_key_google", "ai_key", "smtp_pass" };

    // Column order per table — kept explicit so import/export always agree.
    private static readonly Dictionary<string, string[]> Tables = new()
    {
        ["cards"] = new[] { "id", "name", "due_day", "color", "default_bucket", "default_payer",
                            "carry_mel", "carry_aryn", "stmt_balance", "stmt_balance_at", "note",
                            "archived", "sort", "file_hint", "last4", "card_type", "rewards" },
        ["txns"] = new[] { "id", "card_id", "txn_date", "post_date", "description", "category",
                           "amount", "bucket", "note", "needs_review", "settled_id", "source",
                           "hash", "created_at", "flag_reason", "flag_by", "receipt" },
        ["settlements"] = new[] { "id", "card_id", "settled_at", "label", "shared_total", "mel_total",
                                  "aryn_total", "mel_part", "aryn_part", "payments", "note", "by" },
    };

    public static void AppendRawSheets(XLWorkbook wb)
    {
        using var con = Db.Open();
        foreach (var (table, cols) in Tables)
            DumpTable(wb, con, table, cols);

        var meta = wb.AddWorksheet("_meta");
        meta.Cell(1, 1).Value = "k"; meta.Cell(1, 2).Value = "v";
        int r = 2;
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT k, v FROM meta";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            string k = rd.GetString(0);
            if (SecretMetaKeys.Contains(k) || k.StartsWith("detected_pay:")) continue;
            meta.Cell(r, 1).Value = k;
            meta.Cell(r, 2).Value = rd.IsDBNull(1) ? "" : rd.GetString(1);
            r++;
        }
        // The raw sheets are for the app, not the eye — hide them.
        foreach (var name in new[] { "_cards", "_txns", "_settlements", "_meta" })
            if (wb.Worksheets.TryGetWorksheet(name, out var ws)) ws.Hide();
    }

    private static void DumpTable(XLWorkbook wb, SqliteConnection con, string table, string[] cols)
    {
        var ws = wb.AddWorksheet("_" + table);
        for (int c = 0; c < cols.Length; c++) ws.Cell(1, c + 1).Value = cols[c];
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(",", cols)} FROM {table}";
        using var rd = cmd.ExecuteReader();
        int r = 2;
        while (rd.Read())
        {
            for (int c = 0; c < cols.Length; c++)
            {
                if (rd.IsDBNull(c)) continue;
                ws.Cell(r, c + 1).SetValue(XLCellValue.FromObject(rd.GetValue(c)));
            }
            r++;
        }
    }

    /// <summary>Wipe and rebuild every table from a backup workbook. Returns row counts.</summary>
    public static (int cards, int txns, int settlements) Restore(Stream xlsx)
    {
        using var wb = new XLWorkbook(xlsx);
        if (!wb.Worksheets.Contains("_cards") || !wb.Worksheets.Contains("_txns"))
            throw new InvalidOperationException(
                "This file has no save-point data — use an Excel exported by SplitStatement.");

        using var con = Db.Open();
        using var tx = con.BeginTransaction();
        // Order matters for the FKs: clear children → parents, restore parents → children.
        foreach (var t in new[] { "txns", "settlements", "cards" })
            con.Execute($"DELETE FROM {table_ok(t)}", transaction: tx);
        con.Execute("DELETE FROM meta WHERE k NOT IN @keep",
            new { keep = SecretMetaKeys.Concat(new[] { "smtp_host", "smtp_port", "smtp_user", "email_to" }).ToArray() }, tx);

        int cards = LoadTable(wb, con, tx, "cards");
        int settlements = LoadTable(wb, con, tx, "settlements");
        int txns = LoadTable(wb, con, tx, "txns");

        if (wb.Worksheets.TryGetWorksheet("_meta", out var meta))
            foreach (var row in meta.RowsUsed().Skip(1))
            {
                string k = row.Cell(1).GetString();
                if (k.Length == 0 || SecretMetaKeys.Contains(k)) continue;
                con.Execute("INSERT INTO meta (k,v) VALUES (@k,@v) ON CONFLICT(k) DO UPDATE SET v=@v",
                    new { k, v = row.Cell(2).GetString() }, tx);
            }
        tx.Commit();
        return (cards, txns, settlements);
    }

    private static string table_ok(string t) =>
        t is "txns" or "settlements" or "cards" ? t : throw new ArgumentException(t);

    private static int LoadTable(XLWorkbook wb, SqliteConnection con, IDbTransaction tx, string table)
    {
        if (!wb.Worksheets.TryGetWorksheet("_" + table, out var ws)) return 0;
        var header = ws.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        var known = Tables[table];
        var cols = header.Where(h => known.Contains(h)).ToList();
        if (cols.Count == 0) return 0;
        string sql = $"INSERT INTO {table_ok(table)} ({string.Join(",", cols)}) " +
                     $"VALUES ({string.Join(",", cols.Select(c => "@" + c))})";
        int n = 0;
        foreach (var row in ws.RowsUsed().Skip(1))
        {
            var p = new Dictionary<string, object?>();
            for (int i = 0; i < header.Count; i++)
            {
                string h = header[i];
                if (!known.Contains(h)) continue;
                var cell = row.Cell(i + 1);
                p[h] = cell.IsEmpty() ? null : CellValue(cell);
            }
            con.Execute(sql, p, tx);
            n++;
        }
        return n;
    }

    private static object? CellValue(IXLCell cell) => cell.DataType switch
    {
        XLDataType.Number => cell.GetDouble(),
        XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss"),
        XLDataType.Boolean => cell.GetBoolean() ? 1 : 0,
        _ => cell.GetString(),
    };
}
