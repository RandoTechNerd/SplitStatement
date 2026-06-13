using ClosedXML.Excel;
using Dapper;

namespace MoneySplit;

/// <summary>
/// Exports the data back into the familiar "Money Split" tab shape: one tab per card per
/// settlement (named like "610-AMZ"), plus an "open-<card>" tab for whatever isn't settled
/// yet. Columns mirror the original workbook: dates, description, note, Shared/Mel/Aryn,
/// with the split math at the bottom.
/// </summary>
public static class ExcelExport
{
    private record TxnRow(string? txn_date, string? post_date, string description, string? category,
                          double amount, string bucket, string? note);

    public static byte[] Build()
    {
        using var con = Db.Open();
        using var wb = new XLWorkbook();

        var cards = con.Query<(long id, string name, int? due_day, double carry_mel, double carry_aryn)>(
            "SELECT id, name, due_day, carry_mel, carry_aryn FROM cards WHERE archived = 0 ORDER BY sort, name");

        foreach (var card in cards)
        {
            // open charges first…
            var open = con.Query<TxnRow>("""
                SELECT txn_date, post_date, description, category, amount, bucket, note
                FROM txns WHERE card_id = @id AND settled_id IS NULL ORDER BY txn_date DESC
                """, new { id = card.id }).ToList();
            if (open.Count > 0)
                WriteTab(wb, Sanitize($"open-{card.name}"), card.due_day, open,
                         card.carry_mel, card.carry_aryn);

            // …then each settled cycle.
            var settlements = con.Query<(long id, string? label, string settled_at)>("""
                SELECT id, label, settled_at FROM settlements WHERE card_id = @id ORDER BY settled_at DESC
                """, new { id = card.id });
            foreach (var s in settlements)
            {
                var rows = con.Query<TxnRow>("""
                    SELECT txn_date, post_date, description, category, amount, bucket, note
                    FROM txns WHERE settled_id = @sid ORDER BY txn_date DESC
                    """, new { sid = s.id }).ToList();
                if (rows.Count == 0) continue;
                string label = string.IsNullOrWhiteSpace(s.label)
                    ? DateTime.Parse(s.settled_at).ToString("Mdd")
                    : s.label;
                WriteTab(wb, Sanitize($"{label}-{card.name}"), card.due_day, rows, 0, 0);
            }
        }

        // Hidden raw sheets turn this file into a restorable save-point.
        BackupService.AppendRawSheets(wb);

        if (!wb.Worksheets.Any()) wb.AddWorksheet("empty").Cell(1, 1).Value = "No data yet.";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Auto-export: one settled cycle as its own workbook (called after each settle).</summary>
    public static void ExportCycle(long settlementId, string dir)
    {
        using var con = Db.Open();
        var s = con.QueryFirstOrDefault<(long cardId, string? label, string settledAt)?>(
            "SELECT card_id, label, settled_at FROM settlements WHERE id = @id", new { id = settlementId });
        if (s == null) return;
        var card = con.QueryFirst<(string name, int? dueDay)>(
            "SELECT name, due_day FROM cards WHERE id = @id", new { id = s.Value.cardId });
        var rows = con.Query<TxnRow>("""
            SELECT txn_date, post_date, description, category, amount, bucket, note
            FROM txns WHERE settled_id = @sid ORDER BY txn_date DESC
            """, new { sid = settlementId }).ToList();
        if (rows.Count == 0) return;

        string label = string.IsNullOrWhiteSpace(s.Value.label)
            ? DateTime.Parse(s.Value.settledAt).ToString("Mdd") : s.Value.label!;
        using var wb = new XLWorkbook();
        WriteTab(wb, Sanitize($"{label}-{card.name}"), card.dueDay, rows, 0, 0);
        Directory.CreateDirectory(dir);
        wb.SaveAs(System.IO.Path.Combine(dir, Sanitize($"{label}-{card.name}").Trim() + ".xlsx"));
    }

    private static void WriteTab(XLWorkbook wb, string name, int? dueDay, List<TxnRow> rows,
                                 double carryMel, double carryAryn)
    {
        // Two cycles can share a label (e.g. two "Catch-up" settlements) — keep names unique.
        string unique = name; int n = 2;
        while (wb.Worksheets.Contains(unique)) unique = Sanitize($"{name} {n++}");
        var ws = wb.AddWorksheet(unique);
        ws.Cell(1, 1).Value = dueDay is int d ? $"DUE {d}{Ordinal(d)}" : "";
        ws.Cell(1, 1).Style.Font.SetBold();

        string[] headers = { "Transaction Date", "Post Date", "Description", "Category", "Note",
                             "Shared", "Mel", "Aryn" };
        for (int c = 0; c < headers.Length; c++)
            ws.Cell(2, c + 1).SetValue(headers[c]).Style.Font.SetBold();

        int r = 3;
        foreach (var t in rows)
        {
            ws.Cell(r, 1).Value = t.txn_date ?? "";
            ws.Cell(r, 2).Value = t.post_date ?? "";
            ws.Cell(r, 3).Value = t.description;
            ws.Cell(r, 4).Value = t.category ?? "";
            ws.Cell(r, 5).Value = t.note ?? "";
            int col = t.bucket switch { "Mel" => 7, "Aryn" => 8, _ => 6 };
            if (t.bucket != "Skip") ws.Cell(r, col).Value = t.amount;
            r++;
        }

        int first = 3, last = Math.Max(r - 1, 3);
        r += 1;
        ws.Cell(r, 5).SetValue("total to split").Style.Font.SetBold();
        ws.Cell(r, 6).FormulaA1 = $"=SUM(F{first}:F{last})";
        ws.Cell(r + 1, 5).SetValue("Mel's part (shared/2 + Mel + carryover)").Style.Font.SetBold();
        ws.Cell(r + 1, 7).FormulaA1 = $"=F{r}/2+SUM(G{first}:G{last})+{carryMel}";
        ws.Cell(r + 2, 5).SetValue("Aryn's part (shared/2 + Aryn + carryover)").Style.Font.SetBold();
        ws.Cell(r + 2, 8).FormulaA1 = $"=F{r}/2+SUM(H{first}:H{last})+{carryAryn}";

        ws.Columns(1, 5).AdjustToContents();
        ws.Columns(6, 8).Width = 11;
        ws.Range(first, 6, last + 3, 8).Style.NumberFormat.Format = "#,##0.00";
    }

    private static string Ordinal(int d) => (d % 100 is 11 or 12 or 13) ? "TH"
        : (d % 10) switch { 1 => "ST", 2 => "ND", 3 => "RD", _ => "TH" };

    private static string Sanitize(string s)
    {
        foreach (char c in @":\/?*[]") s = s.Replace(c, ' ');
        return s.Length > 31 ? s[..31] : s;   // Excel's 31-char tab-name limit
    }
}
