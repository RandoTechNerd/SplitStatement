"""One-time seeder: reads 'Money Split 2026.xlsx' into the MoneySplit SQLite db.

- Creates cards with due days / colors / best-guess default payers.
- Imports every tab's transactions as SETTLED history (one settlement per tab).
- Learns merchant->bucket rules from every historical decision.
Safe to re-run: it wipes and re-seeds only seed-sourced rows.
"""
import os, re, sqlite3, hashlib
import datetime as _dt
import pandas as pd

XLSX = r"C:\Users\Rando\Claude Play\Money Split 2026.xlsx"
DB = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "moneysplit.db")

SCHEMA = """
CREATE TABLE IF NOT EXISTS cards (
    id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE, due_day INTEGER,
    color TEXT DEFAULT '#4F86C6', default_bucket TEXT DEFAULT 'Shared',
    default_payer TEXT DEFAULT 'Aryn', carry_mel REAL DEFAULT 0, carry_aryn REAL DEFAULT 0,
    stmt_balance REAL, stmt_balance_at TEXT, note TEXT, archived INTEGER DEFAULT 0,
    sort INTEGER DEFAULT 0);
CREATE TABLE IF NOT EXISTS txns (
    id INTEGER PRIMARY KEY AUTOINCREMENT, card_id INTEGER NOT NULL REFERENCES cards(id),
    txn_date TEXT, post_date TEXT, description TEXT NOT NULL, category TEXT,
    amount REAL NOT NULL, bucket TEXT NOT NULL DEFAULT 'Shared', note TEXT,
    needs_review INTEGER DEFAULT 0, settled_id INTEGER REFERENCES settlements(id),
    source TEXT, hash TEXT, created_at TEXT DEFAULT (datetime('now')));
CREATE INDEX IF NOT EXISTS idx_txns_card ON txns(card_id, settled_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_txns_hash ON txns(hash) WHERE hash IS NOT NULL;
CREATE TABLE IF NOT EXISTS rules (
    merchant TEXT PRIMARY KEY, shared_n INTEGER DEFAULT 0, mel_n INTEGER DEFAULT 0,
    aryn_n INTEGER DEFAULT 0, updated_at TEXT DEFAULT (datetime('now')));
CREATE TABLE IF NOT EXISTS settlements (
    id INTEGER PRIMARY KEY AUTOINCREMENT, card_id INTEGER REFERENCES cards(id),
    settled_at TEXT DEFAULT (datetime('now')), label TEXT,
    shared_total REAL, mel_total REAL, aryn_total REAL, mel_part REAL, aryn_part REAL,
    payments TEXT, note TEXT, by TEXT);
CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT);
"""

CARDS = {  # name: (due_day, color, default_payer, default_bucket, note)
    "AK Air":      (15, "#2E6FB7", "Mel",  "Shared", "BoA Alaska Airlines"),
    "AAA":         (19, "#C44536", "Both", "Shared", ""),
    "ARAMEX PREF": (4,  "#7F77DD", "Both", "Shared", ""),
    "AMZ":         (11, "#E8920D", "Aryn", "Shared", "Chase Amazon"),
    "MEL Amex":    (1,  "#B3589A", "Mel",  "Shared", ""),
    "Cap 1":       (15, "#1F9D55", "Aryn", "Shared", ""),
    "Chase":       (19, "#3E4C8C", "Aryn", "Shared", ""),
    "BILT":        (20, "#444B54", "Aryn", "Shared", ""),
    "WellsFargo":  (20, "#A33F3F", "Aryn", "Shared", "new card"),
    "Aryn new Chase": (24, "#6B7280", "Aryn", "Aryn", "personal — not split (DO NOT COPY)"),
}

TAB_CARD_ALIASES = {
    "AK AIR": "AK Air", "AAA": "AAA", "ARAMEX PREF": "ARAMEX PREF", "AMZ": "AMZ",
    "MEL AMEX": "MEL Amex", "CAP 1": "Cap 1", "CHASE": "Chase", "BILT": "BILT",
    "WELLSFARGO": "WellsFargo", "ARYN NEW CHASE": "Aryn new Chase",
}

SKIP_DESC = re.compile(r"balance|total|leftover|forward|payment thank you|autopay", re.I)

def norm_merchant(desc: str) -> str:
    s = desc.upper()
    s = re.sub(r"\*\S*", " ", s)
    s = re.sub(r"[^A-Z]+", " ", s)
    s = re.sub(r"\b(APLPAY|TST|SQ|SP|PAYPAL)\b", " ", s)
    words = s.split()
    return " ".join(words[:3])

def tab_parts(name: str):
    m = re.match(r"\s*([0-9]+|TBD)\s*-?\s*(.+?)\s*$", name)
    if not m: return None, None
    label, rest = m.group(1), m.group(2).strip().upper()
    card = TAB_CARD_ALIASES.get(rest)
    if card is None:
        for k, v in TAB_CARD_ALIASES.items():
            if rest.startswith(k) or k in rest: card = v; break
    return label, card

def label_date(label: str) -> str:
    # '510' -> 2026-05-10, '44' -> 2026-04-04, '128' -> 2026-01-28, '15' -> 2026-01-05
    if not label.isdigit(): return "2026-01-01"
    month, day = int(label[0]), int(label[1:])
    if not (1 <= month <= 12 and 1 <= day <= 31): return "2026-01-01"
    return f"2026-{month:02d}-{day:02d}"

def main():
    os.makedirs(os.path.dirname(DB), exist_ok=True)
    con = sqlite3.connect(DB)
    cur = con.cursor()
    cur.executescript(SCHEMA)

    # clean previous seed runs (un-claim imported rows that point at seed settlements first)
    cur.execute("""UPDATE txns SET settled_id = NULL WHERE source NOT LIKE 'seed:%' AND settled_id IN
                   (SELECT id FROM settlements WHERE note = '[seeded from workbook]')""")
    cur.execute("DELETE FROM txns WHERE source LIKE 'seed:%'")
    cur.execute("DELETE FROM settlements WHERE note = '[seeded from workbook]'")
    cur.execute("DELETE FROM rules")

    card_ids = {}
    for name, (due, color, payer, bucket, note) in CARDS.items():
        cur.execute("""INSERT INTO cards (name, due_day, color, default_payer, default_bucket, note)
                       VALUES (?,?,?,?,?,?)
                       ON CONFLICT(name) DO UPDATE SET due_day=excluded.due_day""",
                    (name, due, color, payer, bucket, note))
        card_ids[name] = cur.execute("SELECT id FROM cards WHERE name=?", (name,)).fetchone()[0]

    xl = pd.ExcelFile(XLSX)
    rules: dict[str, list[int]] = {}
    tabs = txn_count = claimed = 0

    for sheet in xl.sheet_names:
        label, cardname = tab_parts(sheet)
        if cardname is None or cardname == "Aryn new Chase":  # personal card: skip history
            continue
        df = pd.read_excel(XLSX, sheet_name=sheet, header=None)
        if df.empty: continue

        # find the header row + bucket columns
        hdr, cols = None, {}
        for i in range(min(6, len(df))):
            row = {j: str(v).strip().lower() for j, v in enumerate(df.iloc[i]) if pd.notna(v)}
            names = set(row.values())
            if "shared" in names:
                hdr = i
                for j, v in row.items():
                    if v == "shared": cols["Shared"] = j
                    elif v == "mel": cols["Mel"] = j
                    elif v == "aryn": cols["Aryn"] = j
                    elif "description" in v: cols["desc"] = j
                    elif v in ("type",): cols["note"] = j
                    elif "category" in v: cols["cat"] = j
                break
        if hdr is None or "Shared" not in cols: continue
        # ARAMEX-style tabs have no "Description" header: date | desc | member | acct | buckets
        if "desc" not in cols:
            cols["desc"] = 1

        rows = []
        for i in range(hdr + 1, len(df)):
            r = df.iloc[i]
            desc = r[cols["desc"]] if "desc" in cols and cols["desc"] < len(r) else None
            if pd.isna(desc) or not str(desc).strip(): continue
            desc = str(desc).strip()
            if SKIP_DESC.search(desc): continue
            # the bucket = whichever amount column is populated
            bucket = amount = None
            for b in ("Shared", "Mel", "Aryn"):
                j = cols.get(b)
                if j is None or j >= len(r): continue
                v = r[j]
                if pd.notna(v) and isinstance(v, (int, float)) and v != 0:
                    bucket, amount = b, float(v)
                    break
            if bucket is None: continue
            # dates: first date-like cell — Timestamp, plain datetime, or text in either format
            tdate = None
            for v in r[:4]:
                if isinstance(v, (pd.Timestamp, _dt.datetime, _dt.date)):
                    tdate = pd.Timestamp(v).date().isoformat(); break
                s = str(v).strip()
                m = re.match(r"^(\d{1,2})/(\d{1,2})/(\d{4})$", s)
                if m:
                    tdate = f"{m.group(3)}-{int(m.group(1)):02d}-{int(m.group(2)):02d}"; break
                m = re.match(r"^(\d{4})-(\d{2})-(\d{2})", s)
                if m:
                    tdate = f"{m.group(1)}-{m.group(2)}-{m.group(3)}"; break
            note = None
            if "note" in cols and cols["note"] < len(r) and pd.notna(r[cols["note"]]):
                nv = str(r[cols["note"]]).strip()
                if nv.lower() not in ("payment", "sale", "return"): note = nv
            cat = None
            if "cat" in cols and cols["cat"] < len(r) and pd.notna(r[cols["cat"]]):
                cat = str(r[cols["cat"]]).strip()
            rows.append((tdate, desc, cat, amount, bucket, note))

        if not rows: continue

        # normalize signs: charges should be positive
        neg = sum(1 for x in rows if x[3] < 0)
        if neg > len(rows) / 2:
            rows = [(d, ds, c, -a, b, n) for (d, ds, c, a, b, n) in rows]

        cid = card_ids[cardname]
        shared_t = round(sum(r[3] for r in rows if r[4] == "Shared"), 2)
        mel_t = round(sum(r[3] for r in rows if r[4] == "Mel"), 2)
        aryn_t = round(sum(r[3] for r in rows if r[4] == "Aryn"), 2)
        cur.execute("""INSERT INTO settlements (card_id, settled_at, label, shared_total, mel_total,
                       aryn_total, mel_part, aryn_part, note, by)
                       VALUES (?,?,?,?,?,?,?,?,?,?)""",
                    (cid, label_date(label or ""), label, shared_t, mel_t, aryn_t,
                     round(shared_t / 2 + mel_t, 2), round(shared_t / 2 + aryn_t, 2),
                     "[seeded from workbook]", "seed"))
        sid = cur.lastrowid

        for (tdate, desc, cat, amount, bucket, note) in rows:
            norm_desc = re.sub(r"\s+", " ", desc.upper()).strip()
            key = f"{cid}|{tdate if tdate else ''}|{norm_desc}|{amount:.2f}"
            h = hashlib.sha256(key.encode()).hexdigest()[:24].upper()
            # Every workbook decision teaches the merchant memory, claimed or inserted.
            m = norm_merchant(desc)
            if m:
                rules.setdefault(m, [0, 0, 0])
                rules[m][("Shared", "Mel", "Aryn").index(bucket)] += 1
            try:
                cur.execute("""INSERT INTO txns (card_id, txn_date, description, category, amount,
                               bucket, settled_id, source, hash) VALUES (?,?,?,?,?,?,?,?,?)""",
                            (cid, tdate, desc, cat, round(amount, 2), bucket, sid, f"seed:{sheet}", h))
            except sqlite3.IntegrityError:
                # Same charge already imported from a statement file. The workbook is the
                # source of truth for settled history: claim the open row into this cycle.
                cur.execute("""UPDATE txns SET settled_id = ?, bucket = ?, needs_review = 0
                               WHERE hash = ? AND settled_id IS NULL""", (sid, bucket, h))
                claimed += cur.rowcount
                continue
            txn_count += 1
        tabs += 1

    for m, (s, me, a) in rules.items():
        cur.execute("""INSERT INTO rules (merchant, shared_n, mel_n, aryn_n) VALUES (?,?,?,?)
                       ON CONFLICT(merchant) DO UPDATE SET shared_n=?, mel_n=?, aryn_n=?""",
                    (m, s, me, a, s, me, a))

    # Fuzzy reconciliation: a statement row and its workbook twin can disagree by a day or
    # two (transaction vs posted date) or in wording. Claim open import rows that match a
    # seeded settled row by exact amount, ±3 days, similar merchant — and drop the seed twin
    # in favor of the real statement row.
    fuzzy = 0
    seed_rows = cur.execute("""SELECT id, card_id, txn_date, amount, bucket, settled_id, description
                               FROM txns WHERE source LIKE 'seed:%' AND txn_date IS NOT NULL""").fetchall()
    for srow_id, card, d, amt, bucket, settle, sdesc in seed_rows:
        skey = norm_merchant(sdesc)[:4]
        cand = cur.execute("""
            SELECT id, description FROM txns
            WHERE card_id = ? AND settled_id IS NULL AND source LIKE 'import:%'
              AND ABS(amount - ?) < 0.005 AND txn_date IS NOT NULL
              AND ABS(julianday(txn_date) - julianday(?)) <= 3
            ORDER BY ABS(julianday(txn_date) - julianday(?)) LIMIT 1""",
            (card, amt, d, d)).fetchone()
        if not cand: continue
        if skey and not norm_merchant(cand[1]).startswith(skey): continue
        cur.execute("UPDATE txns SET settled_id = ?, bucket = ?, needs_review = 0 WHERE id = ?",
                    (settle, bucket, cand[0]))
        cur.execute("DELETE FROM txns WHERE id = ?", (srow_id,))
        fuzzy += 1

    con.commit()
    print(f"Seeded {tabs} tabs, {txn_count} transactions, {len(rules)} merchant rules into {DB}")
    print(f"Claimed {claimed} already-imported open charges into workbook settlements (exact)")
    print(f"Claimed {fuzzy} more via fuzzy match (amount + ±3 days + merchant)")

    top = cur.execute("""SELECT merchant, shared_n, mel_n, aryn_n FROM rules
                         ORDER BY shared_n+mel_n+aryn_n DESC LIMIT 12""").fetchall()
    for t in top: print("  rule:", t)
    con.close()

if __name__ == "__main__":
    main()
