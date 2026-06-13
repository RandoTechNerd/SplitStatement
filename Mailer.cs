using Dapper;
using System.Net.Mail;
using System.Net.Mime;

namespace MoneySplit;

/// <summary>
/// One SMTP door for everything: the test button, the AI's send_email tool, and the
/// due-date reminder engine. Messages ship in a Material 3 ("Material You") layout —
/// tonal surfaces instead of borders, large radii, pill CTA — with the app logo embedded
/// as an inline CID attachment (remote images don't survive Gmail's proxy for a LAN app).
/// All styles inline (email clients strip stylesheets).
/// </summary>
public static class Mailer
{
    private static string Get(string k)
    {
        using var con = Db.Open();
        return con.QueryFirstOrDefault<string>("SELECT v FROM meta WHERE k = @k", new { k }) ?? "";
    }

    /// <summary>Plain-text in (AI tool, simple callers) → one tonal card out.</summary>
    public static (bool Ok, string? Error) Send(string subject, string body, string? to = null) =>
        SendHtml(subject,
            Bubble(null, System.Net.WebUtility.HtmlEncode(body).Replace("\n", "<br>")), to);

    public static (bool Ok, string? Error) SendHtml(string subject, string sectionsHtml,
                                                    string? to = null, string? tip = null)
    {
        string host = Get("smtp_host"), user = Get("smtp_user"), pass = Get("smtp_pass");
        string dest = string.IsNullOrWhiteSpace(to) ? Get("email_to") : to.Trim();
        if (host.Length == 0 || dest.Length == 0) return (false, "SMTP host or recipient not configured");
        try
        {
            using var client = new SmtpClient(host, int.TryParse(Get("smtp_port"), out var p) ? p : 587)
            {
                EnableSsl = true,
                Credentials = new System.Net.NetworkCredential(user, pass),
            };
            string logoPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "favicon.png");
            bool hasLogo = File.Exists(logoPath);
            string html = Template(sectionsHtml, tip, hasLogo);

            using var msg = new MailMessage(user, dest) { Subject = subject, IsBodyHtml = true };
            if (hasLogo)
            {
                var view = AlternateView.CreateAlternateViewFromString(html, null, MediaTypeNames.Text.Html);
                view.LinkedResources.Add(new LinkedResource(logoPath, "image/png") { ContentId = "ssLogo" });
                msg.AlternateViews.Add(view);
            }
            else msg.Body = html;
            client.Send(msg);
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ---------- Material 3 building blocks (tonal surfaces, 24px radii, no borders) ----------
    /// <summary>A content card. Tone via background, not strokes.</summary>
    public static string Bubble(string? title, string innerHtml,
                                string bg = "#ffffff", string titleColor = "#5f6368", string textColor = "#1f1f1f") =>
        $"""
        <div style="background:{bg};border-radius:24px;padding:22px 24px;margin:0 0 12px;">
          {(title == null ? "" : $"<div style=\"font-size:12.5px;font-weight:600;color:{titleColor};margin:0 0 8px;font-family:'Google Sans',Roboto,'Segoe UI',Arial,sans-serif;\">{title}</div>")}
          <div style="font-size:14.5px;line-height:1.65;color:{textColor};">{innerHtml}</div>
        </div>
        """;

    /// <summary>Tonal primary card — the hero (big number, key message).</summary>
    public static string HeroBubble(string? title, string innerHtml) =>
        Bubble(title, innerHtml, bg: "#e8f0fe", titleColor: "#174ea6", textColor: "#0b2a66");

    /// <summary>Tonal amber "heads up" card for urgency.</summary>
    public static string AlertBubble(string title, string innerHtml) =>
        Bubble(title, innerHtml, bg: "#fef7e0", titleColor: "#7a4f01", textColor: "#5c3d00");

    /// <summary>One label/value line for inside a card (right-aligned value).</summary>
    public static string Row(string label, string value, bool bold = false) =>
        $"""
        <table width="100%" cellpadding="0" cellspacing="0" style="border-collapse:collapse;"><tr>
          <td style="font-size:14px;color:#5f6368;padding:4px 0;">{label}</td>
          <td align="right" style="font-size:14px;color:#1f1f1f;padding:4px 0;{(bold ? "font-weight:700;" : "font-weight:500;")}">{value}</td>
        </tr></table>
        """;

    private static string Template(string sections, string? tip, bool hasLogo)
    {
        string logo = hasLogo
            ? "<img src=\"cid:ssLogo\" width=\"30\" height=\"30\" alt=\"\" style=\"border-radius:8px;display:inline-block;vertical-align:middle;margin-right:11px;\">"
            : "";
        string tipHtml = tip == null ? "" : Bubble("&#128161; Tip", tip, bg: "#e6f4ea", titleColor: "#0d652d", textColor: "#0d4023");
        return $"""
            <!DOCTYPE html>
            <html><body style="margin:0;padding:0;background:#f0f4f9;">
            <div style="max-width:560px;margin:0 auto;padding:28px 16px;font-family:'Google Sans',Roboto,'Segoe UI',Arial,sans-serif;">
              <div style="padding:4px 10px 18px;">
                {logo}<span style="font-size:19px;font-weight:600;color:#1f1f1f;vertical-align:middle;letter-spacing:-0.2px;">SplitStatement</span>
              </div>
              {sections}
              {tipHtml}
              <div style="text-align:center;padding:14px 0 6px;">
                <a href="http://split.local" style="display:inline-block;background:#0b57d0;color:#ffffff;border-radius:24px;padding:13px 30px;font-size:14px;font-weight:600;text-decoration:none;font-family:'Google Sans',Roboto,'Segoe UI',Arial,sans-serif;">Open SplitStatement</a>
              </div>
              <div style="text-align:center;color:#80868b;font-size:11.5px;padding:12px 0 6px;line-height:1.6;">
                Sent by SplitStatement on your home network<br>
                <a href="http://split.local" style="color:#0b57d0;text-decoration:none;">split.local</a>
              </div>
            </div>
            </body></html>
            """;
    }
}
