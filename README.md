# SplitStatement 💳

A self-hosted app for two people to **split credit-card statements** — drop in your CSV/Excel
exports, and it sorts every charge into *Split / Person A / Person B*, tracks who owes whom,
checks your books against the bank to the penny, and settles up. Runs entirely on your own
machine; nothing leaves your network except optional AI/email calls you configure with your
own keys.

Built as a friendlier, smarter replacement for a shared spreadsheet.

![cards](docs/screenshot.png)

## Features

- **Flip-through card wallet** with realistic card faces, due-date badges, and live balances.
- **Smart import** — Chase / Amex / Capital One / AAA / BILT / generic CSV & Excel. Auto-detects
  the issuer, normalizes signs, skips payment rows, dedupes overlapping exports, and routes files
  to the right card by filename, embedded card number, or header fingerprint.
- **Learns your splits** — every bucket decision teaches a merchant memory; recurring charges and
  per-card owner numbers auto-sort; only the unsure ones are flagged for review.
- **Balance check** — type the bank's current balance and see instantly whether your charges
  match, with discrepancies decomposed.
- **Settle up** — records how it was actually paid (paid the card / venmo sent / requested / bank),
  carryovers, notes; nets everything into one "who owes whom" number.
- **Card catalog** — knows common cards' reward structures and looks; unknown cards prompt you to
  classify them.
- **Optional AI assistant** (your Anthropic or Google key) — investigates charges, runs fraud and
  rewards-optimization checks, reads screenshots, sends emails, and edits buckets via natural language.
- **Optional email** — themed due-date reminders and summaries over your own SMTP.
- **Excel save-point** — every export doubles as a full backup you can restore into a fresh install.
- **Mobile-friendly** over your home wifi at `http://split.local`.

## Run it

Needs the [.NET 9 SDK](https://dotnet.microsoft.com/download) to build from source:

```bash
cd MoneySplit
dotnet run
```

Then open **http://localhost:5275** (or `http://split.local` from any device on the same wifi).

### Or grab the prebuilt app
A self-contained Windows `.exe` (no .NET needed) is published under Releases — unzip anywhere
and run `MoneySplit.exe`. Great for an always-on mini-PC.

## First-time setup
1. Open ⚙ **Settings** → set **Your names**, the **Statements folder**, and (optional) your
   **AI key** and **SMTP** details.
2. **Add a card** per credit card (or **Restore** from a SplitStatement Excel export to load a
   whole setup at once).
3. Drop your downloaded statement CSVs in the folder and hit **Scan**, or import on each card.
4. Review the flagged charges, enter each card's current balance, and **Settle up** when you pay.

## Your data & privacy
Everything lives in `data/moneysplit.db` (one SQLite file — back it up by copying it, or use the
in-app backup). API keys and SMTP passwords are stored locally and are **excluded** from Excel
exports, so a shared save-point never leaks credentials.

## Tech
.NET 9 minimal API · SQLite (Dapper) · ClosedXML · vanilla-JS SPA · mDNS for `split.local`.
No build step for the front end — it's static files in `wwwroot/`.

## License
MIT — see [LICENSE](LICENSE).
