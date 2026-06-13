/* Money Split — the front end. Vanilla JS, talks to /api/*. */

const $ = (sel) => document.querySelector(sel);
const fmt = (n) => (n < 0 ? "-$" : "$") + Math.abs(n).toFixed(2);

let state = { cards: [], netArynOwesMel: 0 };
let selectedId = +localStorage.getItem("ms_card") || null;
let view = "open";
let who = localStorage.getItem("ms_who") || "Aryn";
let creatorMode = localStorage.getItem("ms_creator") === "true";
let currentTheme = localStorage.getItem("ms_theme") || "light";

// ---------- identity / creator mode ----------
let personA = "Aryn", personB = "Mel";   // display names, configurable in Settings

// Friendly names for known ids — anything the live discovery returns that isn't here
// still shows by its raw id. Order here = display order for the catalog.
const AI_CATALOG = {
  anthropic: [
    ["claude-opus-4-8", "Claude Opus 4.8 — most capable"],
    ["claude-sonnet-4-6", "Claude Sonnet 4.6 — balanced"],
    ["claude-haiku-4-5-20251001", "Claude Haiku 4.5 — fast & cheap"],
    ["claude-3-7-sonnet-latest", "Claude 3.7 Sonnet (legacy)"],
    ["claude-3-5-haiku-latest", "Claude 3.5 Haiku (legacy)"],
  ],
  google: [
    ["gemini-3.5-flash", "Gemini 3.5 Flash — newest, fast"],
    ["gemini-flash-latest", "Gemini Flash — always newest"],
    ["gemini-3.1-pro-preview", "Gemini 3.1 Pro — most capable"],
    ["gemini-3-pro-preview", "Gemini 3 Pro"],
    ["gemini-pro-latest", "Gemini Pro — always newest"],
    ["gemini-2.5-flash", "Gemini 2.5 Flash"],
    ["gemini-2.5-pro", "Gemini 2.5 Pro"],
    ["gemini-flash-lite-latest", "Gemini Flash-Lite — fastest"],
  ],
};
let aiAvailableModels = null;   // ids the current key can call (null = not discovered yet)

const modelLabel = (provider, id) =>
  ((AI_CATALOG[provider] || []).find(([v]) => v === id) || [, id])[1];

function fillModelPicker(provider, selected) {
  const sel = $("#aiModelInput");
  const avail = aiAvailableModels;   // array | null
  // Universe = catalog order, then any discovered model not already listed.
  const ids = (AI_CATALOG[provider] || []).map(([v]) => v);
  if (avail) for (const id of avail) if (!ids.includes(id)) ids.push(id);

  let opts = `<option value="">Auto — best your key can reach</option>`;
  for (const id of ids) {
    const locked = avail && !avail.includes(id);
    opts += `<option value="${esc(id)}"${locked ? " disabled" : ""}>` +
            `${locked ? "🔒 " : ""}${esc(modelLabel(provider, id))}${locked ? " (not on this key)" : ""}</option>`;
  }
  if (selected && !ids.includes(selected) && selected !== "__custom__")
    opts += `<option value="${esc(selected)}">${esc(selected)} (custom)</option>`;
  opts += `<option value="__custom__">Custom model id…</option>`;
  sel.innerHTML = opts;
  // a locked saved model falls back to Auto in the UI
  sel.value = (selected && avail && !avail.includes(selected) && ids.includes(selected)) ? "" : (selected || "");
}

// Ask the provider which models this key can call, then rebuild the picker with locks.
async function refreshModels(provider, selected) {
  $("#aiModelHint").textContent = "checking your key…";
  try {
    const r = await (await fetch("/api/ai/models")).json();
    aiAvailableModels = (r.available && r.available.length) ? r.available : null;
    fillModelPicker(provider, selected);
    $("#aiModelHint").textContent = aiAvailableModels
      ? `✓ ${aiAvailableModels.length} models available on this key`
      : (r.error ? `couldn't list models (${esc(String(r.error).slice(0, 40))})` : "");
  } catch {
    aiAvailableModels = null;
    fillModelPicker(provider, selected);
    $("#aiModelHint").textContent = "";
  }
}

function whoName(name, short = false) {
  if (creatorMode) {
    if (name === "Aryn") return short ? "RTN" : "RandoTechNerd";
    if (name === "Mel") return "Mrs. RTN";
    return mask(name);
  }
  if (name === "Aryn") return personA;
  if (name === "Mel") return personB;
  return name;
}

// Creator mode: real names must never render, even inside card names ("MEL Amex").
function mask(s) {
  if (!creatorMode) return String(s ?? "");
  return String(s ?? "")
    .replace(/melissa/gi, "Mrs. RTN")
    .replace(/\bmel\b/gi, "Mrs. RTN")
    .replace(/aryn/gi, "RTN");
}

// Creator mode: transactions matching the blur list get visually blurred on stream.
let blurTerms = [];
function shouldBlur(...texts) {
  if (!creatorMode || !blurTerms.length) return false;
  const hay = texts.join(" ").toLowerCase();
  return blurTerms.some(t => hay.includes(t));
}
async function loadBlurTerms() {
  try {
    const s = await (await fetch("/api/settings")).json();
    blurTerms = (s.blurList || "").split(",").map(t => t.trim().toLowerCase()).filter(Boolean);
    personA = s.personA || "Aryn";
    personB = s.personB || "Mel";
    aiAvailable = !!s.aiKeySet;
    $("#aiFab").hidden = !aiAvailable;   // the ? helper stays off until a key is added
    $("#insightsBtn").hidden = !aiAvailable;
  } catch { blurTerms = []; }
}

function renderWho() {
  $("#whoAryn").textContent = whoName("Aryn", true);
  $("#whoMel").textContent = whoName("Mel", true);
  $("#whoAryn").classList.toggle("active", who === "Aryn");
  $("#whoMel").classList.toggle("active", who === "Mel");
  document.body.className = creatorMode ? "creator-mode" : "";
  document.body.classList.add(`${currentTheme}-theme`);
}
$("#whoAryn").onclick = (e) => { e.stopPropagation(); who = "Aryn"; localStorage.setItem("ms_who", who); renderWho(); };
$("#whoMel").onclick = (e) => { e.stopPropagation(); who = "Mel"; localStorage.setItem("ms_who", who); renderWho(); };

// Creator mode touches names everywhere, so re-render whatever is on screen.
async function renderAll() {
  renderWho();
  renderNet();
  renderRail();
  if (selectedId) { renderCardHeader(); await loadTxns(); await loadHistory(); }
  else { renderSummary(); await loadReviewQueue(); }
}

// ---------- settings ----------
$("#settingsBtn").onclick = async (e) => {
  e.stopPropagation();
  $("#creatorModeToggle").checked = creatorMode;
  $("#settingsModal").hidden = false;
  try {
    const s = await (await fetch("/api/settings")).json();
    $("#folderPathInput").value = s.statementsFolder || "";
    $("#bankAryn").value = s.arynBank || "";
    $("#bankMel").value = s.melBank || "";
    $("#blurInput").value = s.blurList || "";
    $("#personAInput").value = s.personA || "";
    $("#personBInput").value = s.personB || "";
    const prov = s.aiProvider || "anthropic";
    $("#aiProvider").value = prov;
    $("#aiKeyHint").textContent = `${prov === "google" ? "Google" : "Anthropic"} API key — powers the assistant`;
    $("#aiKeyInput").placeholder = s.aiKeySet
      ? "key saved ✓ (paste to replace)"
      : (prov === "google" ? "AIza…" : "sk-ant-…");
    aiAvailableModels = null;
    fillModelPicker(prov, s.aiModel || "");
    $("#aiMaxSteps").value = s.aiMaxSteps || "16";
    if (s.aiKeySet) refreshModels(prov, s.aiModel || "");
    else $("#aiModelHint").textContent = "add a key to see available models";
    $("#smtpHost").value = s.smtpHost || "";
    $("#smtpPort").value = s.smtpPort || "";
    $("#smtpUser").value = s.smtpUser || "";
    $("#smtpPass").placeholder = s.smtpPassSet ? "saved ✓ (paste to replace)" : "app password";
    $("#emailTo").value = s.emailTo || "";
    $("#remindDays").value = s.remindDays || "0";
    $("#remindProg").checked = !!s.remindProgressive;
    $("#archiveChk").checked = !!s.archiveAfterScan;
    $("#archiveNaming").value = s.archiveNaming || "datecard";
    $("#bankArynLabel").textContent = `${whoName("Aryn")}'s bank — last 4`;
    $("#bankMelLabel").textContent = `${whoName("Mel")}'s bank — last 4`;
    $("#settingsLocalIp").textContent =
      (s.remoteUrl || "http://localhost:5275") + (s.remoteIp ? `  ·  ${s.remoteIp}` : "");
  } catch { $("#settingsLocalIp").textContent = "http://localhost:5275"; }
};
$("#settingsClose").onclick = () => $("#settingsModal").hidden = true;
// Click the dimmed backdrop (anywhere outside the sheet), or press Esc, to close settings.
$("#settingsModal").addEventListener("click", (e) => {
  if (e.target === $("#settingsModal")) $("#settingsModal").hidden = true;
});
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && !$("#settingsModal").hidden) $("#settingsModal").hidden = true;
});
$("#creatorModeToggle").onchange = (e) => {
  creatorMode = e.target.checked;
  localStorage.setItem("ms_creator", creatorMode);
  renderAll();
};
$("#folderPathInput").onchange = (e) =>
  fetch("/api/settings", { method: "POST", headers: { "Content-Type": "application/json" },
                           body: JSON.stringify({ statementsFolder: e.target.value }) });
$("#bankAryn").onchange = (e) =>
  fetch("/api/settings", { method: "POST", headers: { "Content-Type": "application/json" },
                           body: JSON.stringify({ arynBank: e.target.value }) });
$("#bankMel").onchange = (e) =>
  fetch("/api/settings", { method: "POST", headers: { "Content-Type": "application/json" },
                           body: JSON.stringify({ melBank: e.target.value }) });
$("#blurInput").onchange = async (e) => {
  await fetch("/api/settings", { method: "POST", headers: { "Content-Type": "application/json" },
                                 body: JSON.stringify({ blurList: e.target.value }) });
  await loadBlurTerms();
  if (selectedId) await loadTxns();
};

const saveSetting = (body) =>
  fetch("/api/settings", { method: "POST", headers: { "Content-Type": "application/json" },
                           body: JSON.stringify(body) });
$("#personAInput").onchange = async (e) => { await saveSetting({ personA: e.target.value }); await loadBlurTerms(); renderAll(); };
$("#personBInput").onchange = async (e) => { await saveSetting({ personB: e.target.value }); await loadBlurTerms(); renderAll(); };
$("#aiKeyInput").onchange = async (e) => {
  if (!e.target.value.trim()) return;
  await saveSetting({ aiKey: e.target.value.trim() });
  e.target.value = ""; e.target.placeholder = "key saved ✓";
  aiAvailable = true;
  $("#aiFab").hidden = false;
  toast("AI key saved — finding available models…");
  // identify the provider the key belongs to (the backend routes by key shape), then discover.
  const s = await (await fetch("/api/settings")).json();
  $("#aiProvider").value = s.aiProvider;
  await refreshModels(s.aiProvider, s.aiModel || "");
};
$("#aiMaxSteps").onchange = (e) => {
  saveSetting({ aiMaxSteps: e.target.value });
  toast(`Assistant effort: ${e.target.options[e.target.selectedIndex].text.replace("Effort: ", "")}`);
};
$("#aiModelInput").onchange = (e) => {
  if (e.target.value === "__custom__") {
    const id = (prompt("Exact model id (e.g. claude-opus-4-8 or gemini-2.5-pro):", "") || "").trim();
    const provider = $("#aiProvider").value;
    fillModelPicker(provider, id);   // rebuild so the custom id shows as a real option
    saveSetting({ aiModel: id });
    toast(id ? `AI model: ${id} (custom)` : "Back to Auto.");
    return;
  }
  saveSetting({ aiModel: e.target.value });
  toast(`AI model: ${e.target.options[e.target.selectedIndex].text}`);
};
$("#aiProvider").onchange = async (e) => {
  await saveSetting({ aiProvider: e.target.value });
  const s = await (await fetch("/api/settings")).json();   // refresh key status + model list
  aiAvailable = !!s.aiKeySet;
  $("#aiFab").hidden = !aiAvailable;
  $("#insightsBtn").hidden = !aiAvailable;
  $("#aiKeyHint").textContent = `${e.target.value === "google" ? "Google" : "Anthropic"} API key — powers the assistant`;
  $("#aiKeyInput").placeholder = s.aiKeySet ? "key saved ✓ (paste to replace)"
    : (s.aiProvider === "google" ? "AIza…" : "sk-ant-…");
  aiAvailableModels = null;
  fillModelPicker(s.aiProvider, s.aiModel || "");
  if (s.aiKeySet) refreshModels(s.aiProvider, s.aiModel || "");
  else $("#aiModelHint").textContent = "add a key to see available models";
  toast(`AI provider: ${e.target.value === "google" ? "Google" : "Anthropic"}${s.aiKeySet ? " ✓ key on file" : " — add its key"}`);
};
$("#smtpHost").onchange = (e) => saveSetting({ smtpHost: e.target.value });
$("#smtpPort").onchange = (e) => saveSetting({ smtpPort: e.target.value });
$("#smtpUser").onchange = (e) => saveSetting({ smtpUser: e.target.value });
$("#smtpPass").onchange = (e) => { if (e.target.value) { saveSetting({ smtpPass: e.target.value }); e.target.value = ""; e.target.placeholder = "saved ✓"; } };
$("#emailTo").onchange = (e) => saveSetting({ emailTo: e.target.value });
$("#remindDays").onchange = async (e) => {
  await saveSetting({ remindDays: e.target.value });
  toast(e.target.value === "0" ? "Due-date reminders off."
    : `Reminders on — email if a card isn't settled ${e.target.options[e.target.selectedIndex].text.toLowerCase()} its due date.`);
};
$("#remindProg").onchange = async (e) => {
  await saveSetting({ remindProgressive: e.target.checked });
  toast(e.target.checked
    ? "😈 Annoying mode on — lead-time email, a halfway follow-up, and a 🚨 on the due day itself."
    : "Annoying mode off — one reminder per cycle.");
};
$("#archiveChk").onchange = (e) => {
  saveSetting({ archiveAfterScan: e.target.checked });
  toast(e.target.checked ? "Auto-archive on — scanned files move into Archive\\year\\month." : "Auto-archive off — files stay put.");
};
$("#archiveNaming").onchange = (e) => saveSetting({ archiveNaming: e.target.value });
$("#emailTestBtn").onclick = async () => {
  const r = await (await fetch("/api/email/test", { method: "POST" })).json().catch(() => ({ sent: false, error: "request failed" }));
  toast(r.sent ? "Test email sent ✉✓" : `Email failed: ${esc(r.error || "check SMTP settings")}`);
};
$("#backupBtn").onclick = async () => {
  const r = await (await fetch("/api/backup", { method: "POST" })).json();
  toast(`Backup saved: ${esc(r.saved)}`);
};
$("#restoreBtn").onclick = () => $("#restoreFile").click();
$("#restoreFile").onchange = async (e) => {
  const f = e.target.files[0]; e.target.value = "";
  if (!f) return;
  if (!confirm(`Restore from "${f.name}"?\n\nThis REPLACES all current cards, transactions, and settle-ups with the contents of that file. Back up first if unsure.`)) return;
  const fd = new FormData(); fd.append("file", f);
  const r = await (await fetch("/api/restore", { method: "POST", body: fd })).json();
  if (r.restored) {
    toast(`Restored ✓ — ${r.cards} cards, ${r.txns} transactions, ${r.settlements} settle-ups.`);
    $("#settingsModal").hidden = true;
    await refresh();
  } else toast(`Restore failed: ${esc(r.error || "not a SplitStatement export")}`);
};

document.querySelectorAll(".theme-btn").forEach(btn => {
  btn.onclick = () => {
    currentTheme = btn.dataset.theme;
    localStorage.setItem("ms_theme", currentTheme);
    renderWho();
  };
});

// ---------- data ----------
async function refresh(keepTxns = false) {
  state = await (await fetch("/api/state")).json();
  renderNet();
  renderRail();
  if (selectedId && state.cards.some(c => c.id === selectedId)) {
    renderCardHeader();
    if (!keepTxns) await loadTxns();
    updateMemoDisplay();
  } else {
    selectedId = null;
    $("#cardView").hidden = true;
    $("#emptyHint").hidden = true;
    $("#summaryView").hidden = false;
    renderSummary();
    loadRequests();
    loadReviewQueue();
  }
}

async function showHome() {
  selectedId = null;
  localStorage.removeItem("ms_card");
  renderRail();
  $("#emptyHint").hidden = true;
  $("#cardView").hidden = true;
  $("#summaryView").hidden = false;
  renderSummary();
  await loadRequests();
  await loadReviewQueue();
}

function card() { return state.cards.find(c => c.id === selectedId); }

// ---------- net banner ----------
function renderNet() {
  const n = state.netArynOwesMel;
  const el = $("#netBanner");
  if (Math.abs(n) < 0.005) {
    el.innerHTML = `All square 🎉`;
  } else if (n > 0) {
    el.innerHTML = `Settlement: <span class="neg">${esc(whoName("Aryn"))} → ${esc(whoName("Mel"))} ${fmt(n)}</span>`;
  } else {
    el.innerHTML = `Settlement: <span class="neg">${esc(whoName("Mel"))} → ${esc(whoName("Aryn"))} ${fmt(-n)}</span>`;
  }
}

// ---------- home / overview ----------
function renderSummary() {
  const n = state.netArynOwesMel;
  $("#summaryNet").innerHTML = Math.abs(n) < 0.005
    ? "All square right now 🎉"
    : n > 0
      ? `If you settled everything today: <span style="color:var(--neg)">${esc(whoName("Aryn"))} → ${esc(whoName("Mel"))} ${fmt(n)}</span>`
      : `If you settled everything today: <span style="color:var(--neg)">${esc(whoName("Mel"))} → ${esc(whoName("Aryn"))} ${fmt(-n)}</span>`;

  const grid = $("#summaryCards");
  grid.innerHTML = "";
  for (const c of state.cards) {
    const div = document.createElement("button");
    div.className = "sum-card";
    div.innerHTML = `<h3>${esc(mask(c.name))} <span class="editpencil" title="Edit this card">✎</span></h3>
      <div class="sum-stats">
        <div><span>Open charges</span><b>${fmt(c.openTotal)}</b></div>
        <div><span>Due</span><b>${c.dueDay ? `${ord(c.dueDay)} (in ${c.dueInDays}d)` : "—"}</b></div>
        <div><span>${esc(whoName("Mel"))}'s part</span><b>${fmt(c.melPart)}</b></div>
        <div><span>${esc(whoName("Aryn"))}'s part</span><b>${fmt(c.arynPart)}</b></div>
        ${c.reviewCount ? `<div><span>Needs review</span><b style="color:var(--warn)">${c.reviewCount}</b></div>` : ""}
        ${c.discrepancy != null && Math.abs(c.discrepancy) >= 0.01 ? `<div><span>Balance check</span><b style="color:var(--neg)">${fmt(c.discrepancy)} off</b></div>` : ""}
      </div>`;
    div.onclick = () => selectCard(c.id);
    div.querySelector(".editpencil").onclick = (e) => { e.stopPropagation(); openCardEditor(c); };
    grid.appendChild(div);
  }
}

// ---------- card editor (the pencil) ----------
let editCardId = null;
let cardCatalog = [];
async function loadCardCatalog() {
  if (cardCatalog.length) return cardCatalog;
  try { cardCatalog = await (await fetch("/api/card-catalog")).json(); } catch { cardCatalog = []; }
  return cardCatalog;
}
async function openCardEditor(c) {
  editCardId = c.id;
  $("#ecName").value = c.name;
  $("#ecDue").value = c.dueDay || "";
  $("#ecPayer").value = c.defaultPayer || "Aryn";
  $("#ecLast4").value = c.last4 || "";
  await loadCardCatalog();
  $("#ecType").innerHTML = `<option value="">— unknown / pick one —</option>` +
    cardCatalog.map(t => `<option value="${t.key}">${esc(t.name)}</option>`).join("");
  $("#ecType").value = c.cardType || "";
  $("#ecSkin").innerHTML = Object.entries(SKINS).map(([v, label]) =>
    `<option value="${v}">${label}</option>`).join("");
  $("#ecSkin").value = (c.color || "").startsWith("skin:") ? c.color.slice(5) : "auto";
  syncTypeRewards();
  $("#editModal").hidden = false;
}
// Picking a card type fills the reward line and snaps the Look to that card's skin.
function syncTypeRewards() {
  const t = cardCatalog.find(x => x.key === $("#ecType").value);
  $("#ecRewards").textContent = t && t.rewards && t.rewards !== "—" ? "Rewards: " + t.rewards : "";
}
$("#ecType").onchange = () => {
  const t = cardCatalog.find(x => x.key === $("#ecType").value);
  if (t && t.skin) $("#ecSkin").value = t.skin;   // follow the type's look (still overridable below)
  syncTypeRewards();
};
$("#ecCancel").onclick = () => $("#editModal").hidden = true;
$("#ecSave").onclick = async () => {
  const skin = $("#ecSkin").value;
  await fetch(`/api/cards/${editCardId}`, {
    method: "PATCH", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name: $("#ecName").value.trim() || null,
      dueDay: +$("#ecDue").value || null,
      defaultPayer: $("#ecPayer").value,
      last4: normLast4($("#ecLast4").value),
      cardType: $("#ecType").value || null,
      color: skin === "auto" ? "auto" : `skin:${skin}`,
    }),
  });
  $("#editModal").hidden = true;
  await refresh();
};

// ---------- outstanding venmo requests (Home) ----------
async function loadRequests() {
  try {
    const rows = await (await fetch("/api/requests-outstanding")).json();
    const el = $("#reqList");
    const head = $("#reqTitle");
    head.hidden = el.hidden = rows.length === 0;
    el.innerHTML = "";
    for (const r of rows) {
      const div = document.createElement("div");
      div.className = "rqrow";
      div.innerHTML = `<span class="rqcard">${esc(mask(r.card))} ${esc(r.label || "")}</span>
        <span class="rqdesc">${esc(whoName(r.who, true))} owes ${fmt(r.amount)} — requested ${esc(r.settledAt)} (${r.daysOld}d ago)</span>
        <button class="vconfirm">✓ got it</button>`;
      div.querySelector(".vconfirm").onclick = async () => {
        await fetch(`/api/settlements/${r.settlementId}/confirm-payment`, {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ index: r.index }),
        });
        await loadRequests();
      };
      el.appendChild(div);
    }
  } catch { /* fine */ }
}

async function loadReviewQueue() {
  const rows = await (await fetch("/api/review-queue")).json();
  const el = $("#reviewQueue");
  el.innerHTML = rows.length ? "" : `<div style="color:var(--muted)">Nothing needs review 🎉</div>`;
  for (const t of rows) {
    const div = document.createElement("div");
    div.className = "rqrow";
    const segBtns = ["Shared", "Mel", "Aryn", "Skip"].map(b =>
      `<button class="${b === t.bucket ? "on " + b : ""}" data-b="${b}">${b === "Shared" ? "Split" : esc(whoName(b, true))}</button>`).join("");
    const rqBlur = shouldBlur(t.description, t.note || "");
    div.innerHTML = `<span class="rqcard">${esc(mask(t.cardName))}</span>
      <span class="rqdesc${rqBlur ? " blurcell" : ""}">${esc(t.description)}${t.note ? ` <i style="color:var(--muted)">(${esc(t.note)})</i>` : ""}</span>
      <span class="rqamt">${fmt(t.amount)}</span>
      <span class="seg">${segBtns}</span>
      <button class="confirmbtn" title="Bucket is right">✓</button>`;
    div.querySelectorAll(".seg button").forEach(btn => {
      btn.onclick = async () => { await patchTxn(t.id, { bucket: btn.dataset.b }); await refresh(); };
    });
    div.querySelector(".confirmbtn").onclick = async () => { await patchTxn(t.id, { confirm: true }); await refresh(); };
    el.appendChild(div);
  }
}

$("#addCardBtn").onclick = async () => {
  const name = prompt("New card name (e.g. 'Costco Citi'):");
  if (!name || !name.trim()) return;
  const dueRaw = prompt("Due day of the month (1–31, blank if unsure):", "");
  const dueDay = dueRaw && +dueRaw >= 1 && +dueRaw <= 31 ? +dueRaw : null;
  await fetch("/api/cards", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name: name.trim(), dueDay }),
  });
  toast(`${esc(mask(name.trim()))} added — double-click its number area to set the last 4, and import a statement to teach it.`);
  await refresh();
};

$("#batchScanBtn").onclick = async () => {
  const btn = $("#batchScanBtn");
  btn.disabled = true; btn.textContent = "Scanning…";
  try {
    const res = await (await fetch("/api/scan", { method: "POST" })).json();
    toast(importSummaryHtml(res));
  } finally {
    btn.disabled = false; btn.textContent = "🔍 Scan Folder for Statements";
  }
  await refresh();
};

// ---------- card rail ----------
function renderRail() {
  const rail = $("#cardRail");
  rail.innerHTML = "";
  const home = document.createElement("button");
  home.className = "cardtile card-home" + (selectedId == null ? " selected" : "");
  home.innerHTML = `<div class="home-mini">🏠 Home</div><div class="home-icon">🏠</div>`;
  home.onclick = () => showHome();
  rail.appendChild(home);
  for (const c of state.cards) {
    const tile = document.createElement("button");
    const cardClass = getCardClass(c);
    tile.className = `cardtile ${cardClass}` + (c.id === selectedId ? " selected" : "");
    
    // Owner tags ("Aryn:11009") stay hidden on the card face — just a colored dot.
    const entries = (c.last4 || "").trim().split(/\s+/).filter(Boolean).map(t => {
      const m = t.match(/^([A-Za-z]+):(\d{4,6})$/);
      if (m) return { who: m[1].toLowerCase(), d: m[2] };
      const d = t.match(/\d{4,6}/);
      return d ? { who: null, d: d[0] } : null;
    }).filter(Boolean);
    // Statement nudge: due inside 5 days and nothing imported in 2+ weeks.
    const stale = c.dueInDays != null && c.dueInDays <= 5 &&
      (!c.lastImportAt || (Date.now() - new Date(c.lastImportAt)) > 14 * 864e5);
    tile.innerHTML = `
      <div class="tile-top">
        <div class="cname">${esc(mask(c.name))}</div>
        ${!c.cardType ? `<span class="badge unk" title="Card type not set — click to pick it so the look and rewards are right">?</span>` : ""}
        ${stale ? `<span class="nudge" title="Due ${c.dueInDays}d and no fresh import — the statement is probably ready to download">📬</span>` : ""}
        ${c.reviewCount > 0 ? `<span class="badge" title="${c.reviewCount} charge${c.reviewCount === 1 ? "" : "s"} need a review — the app wasn't sure who they belong to. Open the card (or Home) to sort them.">${c.reviewCount}</span>` : ""}
        ${c.discrepancy != null && Math.abs(c.discrepancy) >= 0.01 ? `<span class="badge disc" title="Balance check is off by ${fmt(c.discrepancy)}">${fmt(c.discrepancy)}</span>` : ""}
        <div class="due-tag">${c.dueDay ? `Due ${ord(c.dueDay)}` : ""}</div>
      </div>
      <div class="emv"></div><div class="ctl"></div><div class="netmark"></div>
      <div class="copen">${fmt(c.openTotal)}</div>
      <div class="tile4s${creatorMode ? " blur4" : ""}" title="Double-click to edit. Tag an owner like Aryn:11009 — the name stays hidden but new charges on that number default to that person.">
        ${entries.length ? entries.map(e => `<div class="tile4">${e.who ? `<span class="odot ${e.who === "mel" || e.who === "melissa" ? "mel" : "aryn"}"></span>` : ""}•••• ${esc(e.d)}</div>`).join("")
                         : `<div class="tile4 empty">•••• ----</div>`}
      </div>
    `;

    tile.onclick = () => selectCard(c.id);
    const unk = tile.querySelector(".badge.unk");
    if (unk) unk.onclick = (e) => { e.stopPropagation(); openCardEditor(c); };
    tile.querySelector(".tile4s").ondblclick = async (e) => {
      e.stopPropagation();
      const v = prompt(
        `Card number endings for ${c.name}\n` +
        `• two cards? separate with a space: 1234 5678\n` +
        `• tag an owner for auto-splitting: Aryn:11009 Mel:11017\n` +
        `  (the name stays hidden on the card — charges swiped on that number default to that person)`,
        c.last4 || "");
      if (v === null) return;
      const norm = normLast4(v);
      await fetch(`/api/cards/${c.id}`, { method: "PATCH", headers: { "Content-Type": "application/json" },
                                          body: JSON.stringify({ last4: norm }) });
      toast(norm ? `Saved. Files (and swipes) on ${creatorMode ? "those numbers" : norm.replace(/[A-Za-z]+:/g, "").split(" ").join(" / ")} route to ${esc(mask(c.name))}.` : "Card digits cleared.");
      await refresh(true);
      renderRail();
    };
    rail.appendChild(tile);
  }
}

const SKINS = {
  "auto": "Auto (by name)", "card-chase-amz": "Prime Visa (black)", "card-chase-sap": "Chase blue",
  "card-bilt": "BILT navy map", "card-amex-pref": "Amex Preferred (dark)", "card-amex-blue": "Amex Everyday (blue)",
  "card-gold": "Amex Gold/Platinum (gold)", "card-cap1-ven": "Venture navy", "card-citi": "Citi charcoal",
  "card-discover": "Discover orange", "card-aaa": "AAA cyan", "card-wf": "Wells red-gold",
};

function getCardClass(c) {
  if ((c.color || "").startsWith("skin:")) return c.color.slice(5);   // pencil-edited choice wins
  const n = c.name.toLowerCase();
  if (n.includes("amz") || n.includes("amazon") || n.includes("prime")) return "card-chase-amz";
  if (n.includes("pref")) return "card-amex-pref";
  if (n.includes("amex") || n.includes("blue cash")) return "card-amex-blue";
  if (n.includes("bilt")) return "card-bilt";
  if (n.includes("venture") || n.includes("cap")) return "card-cap1-ven";
  if (n.includes("aaa")) return "card-aaa";
  if (n.includes("wellsfargo") || n.includes("wells") || n.includes("wf")) return "card-wf";
  return "card-chase-sap";
}

async function selectCard(id) {
  selectedId = id;
  localStorage.setItem("ms_card", id);
  renderRail();
  renderCardHeader();
  $("#emptyHint").hidden = true;
  $("#summaryView").hidden = true;
  $("#cardView").hidden = false;
  await loadTxns();
  await loadHistory();
  updateMemoDisplay();
  
  const rail = $("#cardRail");
  const selected = rail.querySelector(".selected");
  if (selected) {
    selected.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }
}

function renderCardHeader() {
  const c = card();
  if (!c) return;
  $("#cvName").textContent = mask(c.name);
  if (creatorMode) {
    $("#cvLast4").value = "(hidden)";
    $("#cvLast4").readOnly = true;
  } else {
    $("#cvLast4").value = c.last4 || "";
    $("#cvLast4").readOnly = false;
  }
  const payerOpts = $("#cvPayer").options;
  payerOpts[0].text = `${whoName("Aryn", true)} pays the bill`;
  payerOpts[1].text = `${whoName("Mel", true)} pays the bill`;
  $("#cvPayer").value = c.defaultPayer || "Aryn";
  $("#cvBalance").value = c.stmtBalance ?? "";
  const parsed = parseNote(c.note);
  cardTags = parsed.tags;
  $("#cvNote").value = parsed.prose;
  strategyEdit = false;          // every card opens in clean read-only mode
  applyStrategyMode();
  renderDiscrepancy();
  renderTotals();
}

// Tags live as (label:color) tokens inside the card note, but are managed as removable
// chips + a "+ tag" button — separate from the prose, so nothing gets lost or left as raw text.
let cardTags = [];
let strategyEdit = false;
const TAG_RE = /\(([^():]{1,30}):\s*(#?[0-9a-f]{3,6}|[a-z]+)\s*\)/gi;
const NAMED_COLORS = ["green", "blue", "pink", "purple", "grey", "red", "orange", "yellow", "teal"];
// Every named color → [background, text]. Inline-styled so they ALWAYS render (no missing CSS class).
const TAG_COLORS = {
  green: ["#dcfce7", "#166534"], blue: ["#dbeafe", "#1e40af"], pink: ["#fce7f3", "#9d174d"],
  purple: ["#f3e8ff", "#6b21a8"], grey: ["#f3f4f6", "#374151"], gray: ["#f3f4f6", "#374151"],
  red: ["#fee2e2", "#991b1b"], orange: ["#ffedd5", "#9a3412"], yellow: ["#fef9c3", "#854d0e"],
  teal: ["#ccfbf1", "#115e59"],
};

function parseNote(note) {
  note = note || "";
  const tags = [];
  let m; TAG_RE.lastIndex = 0;
  while ((m = TAG_RE.exec(note)) !== null) tags.push({ label: m[1].trim(), color: m[2].toLowerCase() });
  const prose = note.replace(TAG_RE, "").replace(/\s+/g, " ").trim();
  return { tags, prose };
}
function composeNote(prose, tags) {
  const toks = tags.map(t => `(${t.label}:${t.color})`).join(" ");
  return [(prose || "").trim(), toks].filter(Boolean).join(" ").trim();
}
function tagStyle(color) {
  let c = (color || "").toLowerCase();
  if (TAG_COLORS[c]) { const [bg, ink] = TAG_COLORS[c]; return `background:${bg};color:${ink}`; }
  let hex = c.startsWith("#") ? c.slice(1) : c;
  if (/^[0-9a-f]{3}$/.test(hex)) hex = hex.split("").map(ch => ch + ch).join("");
  if (!/^[0-9a-f]{6}$/.test(hex)) hex = "888888";
  const [r, g, b] = [0, 2, 4].map(k => parseInt(hex.slice(k, k + 2), 16));
  const ink = (0.299 * r + 0.587 * g + 0.114 * b) > 150 ? "#1c1d22" : "#fff";
  return `background:#${hex};color:${ink}`;
}
function tagChipHtml(t, i) {
  const x = strategyEdit ? `<button class="tagx" data-i="${i}" title="Remove">×</button>` : "";
  return `<span class="tag" style="${tagStyle(t.color)}">${esc(t.label)}${x}</span>`;
}
function renderTagChips() {
  const el = $("#strategyTags");
  el.innerHTML = cardTags.map((t, i) => tagChipHtml(t, i)).join("") +
    (strategyEdit ? `<button class="tagadd" title="Add a tag">+ tag</button>` : "") +
    (!strategyEdit && cardTags.length === 0 ? `<span class="tag-empty">no tags</span>` : "");
  el.querySelectorAll(".tagx").forEach(b => b.onclick = async () => {
    cardTags.splice(+b.dataset.i, 1); renderTagChips(); await saveStrategy();
  });
  const add = el.querySelector(".tagadd");
  if (add) add.onclick = addTagPrompt;
}

// Read-only by default; the pencil flips edit mode (tags get ×/+, prose becomes editable).
function applyStrategyMode() {
  const sec = $(".strategy-section");
  sec.classList.toggle("editing", strategyEdit);
  $("#cvNote").readOnly = !strategyEdit;
  $("#stratEdit").innerHTML = strategyEdit ? "✓" : pencilSvg;
  $("#stratEdit").title = strategyEdit ? "Done editing" : "Edit strategy";
  renderTagChips();
}
const pencilSvg = `<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"/></svg>`;
async function addTagPrompt() {
  const label = (prompt("Tag label  (e.g. 5% groceries):", "") || "").trim().replace(/[():]/g, "");
  if (!label) return;
  const color = (prompt("Color — a name (" + NAMED_COLORS.join(" / ") + ") or hex like 343434:", "green")
    || "green").trim().toLowerCase().replace(/[():\s]/g, "");
  cardTags.push({ label, color });
  renderTagChips(); await saveStrategy();
}
async function saveStrategy() {
  if (!selectedId) return;
  const note = composeNote($("#cvNote").value, cardTags);
  await fetch(`/api/cards/${selectedId}`, { method: "PATCH", headers: { "Content-Type": "application/json" },
                                            body: JSON.stringify({ note }) });
}

function updateMemoDisplay() {
  if (!selectedId) return;
  const memoKey = `ms_card_memo_${selectedId}`;
  $("#stickyNoteArea").value = localStorage.getItem(memoKey) || "";
}

$("#stickyNoteArea").oninput = (e) => {
  if (!selectedId) return;
  localStorage.setItem(`ms_card_memo_${selectedId}`, e.target.value);
};

function renderDiscrepancy() {
  const c = card();
  const el = $("#cvDiscrepancy");
  if (c.stmtBalance == null) { el.textContent = ""; el.className = "chip"; return; }
  const d = Math.round((c.stmtBalance - c.openTotal) * 100) / 100;
  if (Math.abs(d) < 0.01) { el.textContent = "✓ Balanced"; el.className = "chip ok"; }
  else { el.textContent = `${d > 0 ? "+" : ""}${fmt(d)} diff`; el.className = "chip off"; }
}

function renderTotals() {
  const c = card();
  $("#cvTotals").innerHTML = `
    <div class="tot">Shared<b>${fmt(c.sharedTotal)}</b></div>
    <div class="tot">½ each<b>${fmt(c.sharedTotal / 2)}</b></div>
    <div class="tot mel">${esc(whoName("Mel"))} only<b>${fmt(c.melTotal)}</b></div>
    <div class="tot aryn">${esc(whoName("Aryn"))} only<b>${fmt(c.arynTotal)}</b></div>
    ${c.carryMel || c.carryAryn ? `<div class="tot">Carryover<b>M ${fmt(c.carryMel)} / A ${fmt(c.carryAryn)}</b></div>` : ""}
    <div class="tot part mel">${esc(whoName("Mel"))}'s part<b>${fmt(c.melPart)}</b></div>
    <div class="tot part">${esc(whoName("Aryn"))}'s part<b>${fmt(c.arynPart)}</b></div>`;
}

// ---------- transactions ----------
async function loadTxns() {
  const apiView = view === "review" ? "open" : view;
  let rows = await (await fetch(`/api/cards/${selectedId}/txns?view=${apiView}`)).json();
  if (view === "review") rows = rows.filter(r => r.needsReview);
  const body = $("#txnBody");
  body.innerHTML = "";
  if (rows.length === 0) {
    body.innerHTML = `<tr><td colspan="6" style="color:var(--muted);padding:24px;text-align:center">
      ${view === "review" ? "Nothing needs review 🎉" : view === "settled" ? "No settled charges yet." : "No open charges — import a statement!"}</td></tr>`;
    return;
  }

  // The green line: where the last settle-up cuts the list. Charges above it are new
  // since then (≈ the next settle); anything below was already on the books last time.
  const lastSettle = view === "open" ? (card()?.lastSettledAt || "").slice(0, 10) : "";
  let lineDrawn = !lastSettle;

  // Card payments never become transactions (they'd poison the split math), but they
  // SHOULD be visible: merge the detected payments in as informational ghost rows.
  let ghosts = [];
  if (view === "open") {
    try {
      const det = await (await fetch(`/api/cards/${selectedId}/payments-detected`)).json();
      const oldest = rows.length ? (rows[rows.length - 1].txnDate || rows[rows.length - 1].postDate || "") : "";
      ghosts = det
        .map(p => ({ date: p.date ?? p.Date, amount: -(p.amount ?? p.Amount), desc: p.description ?? p.Description }))
        .filter(g => g.date && g.amount && (!oldest || g.date >= oldest))
        .filter(g => !rows.some(r => Math.abs(r.amount - g.amount) < 0.01 &&
          Math.abs(new Date(r.txnDate || r.postDate) - new Date(g.date)) <= 3 * 864e5));
    } catch { /* none */ }
  }

  const entries = [
    ...rows.map(r => ({ kind: "t", date: r.txnDate || r.postDate || "", r })),
    ...ghosts.map(g => ({ kind: "g", date: g.date, g })),
  ].sort((a, b) => a.date < b.date ? 1 : a.date > b.date ? -1 : (a.kind === "g" ? -1 : 1));

  // Estimated even-points: walking oldest→newest through the ledger (charges + payments,
  // buckets ignored — the bank doesn't care), wherever the running sum hits $0 a payment
  // exactly covered everything before it. Dashed line = "you were even here".
  const evenAt = new Set();
  if (view === "open") {
    let cum = 0;
    for (let i = entries.length - 1; i >= 0; i--) {
      cum = Math.round((cum + (entries[i].kind === "t" ? entries[i].r.amount : entries[i].g.amount)) * 100) / 100;
      if (Math.abs(cum) < 0.005) evenAt.add(entries[i]);
    }
  }

  for (const e of entries) {
    if (!lineDrawn && e.date && e.date <= lastSettle) {
      body.appendChild(settleLineRow(lastSettle));
      lineDrawn = true;
    }
    if (evenAt.has(e)) body.appendChild(evenLineRow(e.date));
    body.appendChild(e.kind === "t" ? txnRow(e.r) : ghostRow(e.g));
  }
  if (!lineDrawn) body.appendChild(settleLineRow(lastSettle));
}

function ghostRow(g) {
  const tr = document.createElement("tr");
  tr.className = "payrow";
  tr.title = "Card payment from the statement — shown for the timeline, not part of the split";
  tr.innerHTML = `
    <td style="white-space:nowrap">${esc(g.date)}</td>
    <td class="desc" colspan="2"><div class="d1">💳 ${esc(g.desc)}</div>
        <div class="d2">card payment — informational, not in the split</div></td>
    <td class="amount refund">${fmt(g.amount)}</td>
    <td></td><td></td>`;
  return tr;
}

function evenLineRow(date) {
  const tr = document.createElement("tr");
  tr.className = "evenline";
  tr.innerHTML = `<td colspan="6"><span>≈ estimated even point — the ledger hit $0 here${date ? ` (${esc(date)})` : ""}</span></td>`;
  return tr;
}

function settleLineRow(date) {
  const tr = document.createElement("tr");
  tr.className = "settleline";
  tr.innerHTML = `<td colspan="6"><span>✓ settled up to here on ${esc(date)} — everything above is the next settle-up</span></td>`;
  return tr;
}

function txnRow(t) {
  const tr = document.createElement("tr");
  if (t.needsReview) tr.className = "review";
  if (t.settledId) tr.className = "settledrow";

  const buckets = ["Shared", "Mel", "Aryn", "Skip"];
  const segBtns = buckets.map(b =>
    `<button class="${b === t.bucket ? "on " + b : ""}" data-b="${b}">${b === "Shared" ? "Split" : esc(whoName(b, true))}</button>`).join("");

  const blur = shouldBlur(t.description, t.category || "", t.note || "");
  tr.innerHTML = `
    <td style="white-space:nowrap">${t.txnDate || t.postDate || ""}</td>
    <td class="desc${blur ? " blurcell" : ""}"><div class="d1">${esc(t.description)}</div>
        ${t.category ? `<div class="d2">${esc(t.category)}</div>` : ""}</td>
    <td class="notecell${blur ? " blurcell" : ""}"><input class="note" value="${esc(t.note || "")}" placeholder="What was this?"></td>
    <td class="amount ${t.amount < 0 ? "refund" : ""}">${fmt(t.amount)}</td>
    <td><span class="seg">${segBtns}</span>
        ${t.needsReview ? `<button class="confirmbtn" title="Confirm bucket">✓</button>` : ""}</td>
    <td style="white-space:nowrap">
      <button class="flagbtn ${t.flagBy ? (t.flagBy === "app" ? "on-app" : "on-user") : ""}"
              title="${t.flagBy
                ? esc(mask(t.flagReason || "(no reason given)") + " — flagged by " + (t.flagBy === "app" ? "the app" : whoName(t.flagBy, true)))
                : "Click to flag · double-click to flag with a note"}">⚑</button>
      ${t.receipt ? `<button class="rowbtn rcpt" title="View the attached receipt">📷</button>`
                  : `<button class="rowbtn attach" title="Attach a receipt photo (or PDF)">📎</button>`}
      ${!t.settledId && t.amount < 0 ? `<button class="rowbtn mkpay" title="This is a card payment, not a charge — move it out of the split into the payment timeline">💳</button>` : ""}
      ${t.settledId ? "" : `<button class="rowbtn del" title="Delete">🗑</button>`}</td>`;

  tr.querySelectorAll(".seg button").forEach(btn => {
    btn.onclick = async () => {
      await patchTxn(t.id, { bucket: btn.dataset.b });
      await refresh();
    };
  });
  const confirmBtn = tr.querySelector(".confirmbtn");
  if (confirmBtn) confirmBtn.onclick = async () => { await patchTxn(t.id, { confirm: true }); await refresh(); };

  // Flag: single click toggles, double click opens the reason box. The click waits a beat
  // so a double-click doesn't toggle twice on the way in.
  const flagBtn = tr.querySelector(".flagbtn");
  let flagTimer = null;
  flagBtn.onclick = (e) => {
    e.stopPropagation();
    clearTimeout(flagTimer);
    flagTimer = setTimeout(async () => {
      await patchTxn(t.id, t.flagBy ? { flag: false } : { flag: true, flagBy: who });
      await loadTxns();
      await refresh(true);
    }, 280);
  };
  flagBtn.ondblclick = async (e) => {
    e.stopPropagation();
    clearTimeout(flagTimer);
    const reason = prompt(`Why flag "${t.description}"?\n(shows when you hover the flag)`, t.flagReason || "");
    if (reason === null) return;
    await patchTxn(t.id, { flag: true, flagReason: reason, flagBy: who });
    await loadTxns();
    await refresh(true);
  };
  const note = tr.querySelector("input.note");
  note.onchange = () => patchTxn(t.id, { note: note.value });
  const del = tr.querySelector(".rowbtn.del");
  if (del) del.onclick = async () => {
    if (!confirm(`Delete "${t.description}"?`)) return;
    await fetch(`/api/txns/${t.id}`, { method: "DELETE" });
    await refresh();
  };
  const rc = tr.querySelector(".rowbtn.rcpt");
  if (rc) rc.onclick = () => window.open(`/api/receipts/${t.receipt}`, "_blank");
  const at = tr.querySelector(".rowbtn.attach");
  if (at) at.onclick = () => { receiptTarget = t.id; $("#receiptFile").click(); };
  const mkpay = tr.querySelector(".rowbtn.mkpay");
  if (mkpay) mkpay.onclick = async () => {
    if (!confirm(`Treat "${t.description}" (${fmt(t.amount)}) as a card payment?\nIt leaves the split and joins the payment timeline (greyed 💳 rows).`)) return;
    await fetch(`/api/txns/${t.id}/make-payment`, { method: "POST" });
    toast("Moved to the payment timeline — it no longer touches the split math.");
    await refresh();
  };
  return tr;
}

const patchTxn = (id, body) =>
  fetch(`/api/txns/${id}`, { method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });

// ---------- filters ----------
document.querySelectorAll(".filter").forEach(b => {
  b.onclick = async () => {
    document.querySelectorAll(".filter").forEach(x => x.classList.remove("active"));
    b.classList.add("active");
    view = b.dataset.view;
    await loadTxns();
  };
});

// ---------- sync from web (paste pending charges) ----------
$("#syncWebBtn").onclick = () => {
  if (!selectedId) return;
  $("#syncText").value = "";
  $("#syncModal").hidden = false;
  $("#syncText").focus();
};
$("#syncCancel").onclick = () => $("#syncModal").hidden = true;
$("#syncGo").onclick = async () => {
  const raw = $("#syncText").value;
  $("#syncModal").hidden = true;
  if (!raw.trim() || !selectedId) return;

  const lines = raw.split("\n").map(l => l.trim()).filter(l => l);
  let added = 0, dupes = 0;
  const addedRows = [], skippedRows = [];
  for (let i = 0; i < lines.length; i++) {
    const dateMatch = lines[i].match(/(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2},?\s+\d{4}/i) || lines[i].match(/\d{1,2}\/\d{1,2}\/\d{2,4}/);
    if (!dateMatch) continue;
    const parsedDate = new Date(dateMatch[0].replace(/(\d),(\s)/, "$1$2"));
    const isoDate = isNaN(parsedDate) ? null : `${parsedDate.getFullYear()}-${String(parsedDate.getMonth() + 1).padStart(2, "0")}-${String(parsedDate.getDate()).padStart(2, "0")}`;
    let desc = "", amount = null, innerIso = null;
    for (let j = i + 1; j < Math.min(i + 10, lines.length); j++) {
      const amtMatch = lines[j].match(/[\−-]?\$(\d{1,3}(,\d{3})*(\.\d{2}))/);
      if (amtMatch) {
        amount = parseFloat(amtMatch[1].replace(/,/g, ""));
        if (lines[j].includes("−") || lines[j].includes("-")) amount = -amount;
        desc = lines.slice(i + 1, j).join(" ").split(",")[0].trim();
        break;
      }
    }
    if (amount === null || !desc) continue;
    // Bank pages (AAA especially) pad the block with boilerplate and bury the real
    // merchant + date at the end — strip the noise, prefer text after the last date.
    desc = desc
      .replace(/online transaction information[^.]*\.?/gi, " ")
      .replace(/your transaction total[^.]*\.?/gi, " ")
      .replace(/transaction type|merchant details|purchases\b|posted|pending/gi, " ")
      .replace(/\s+/g, " ").trim();
    const inner = [...desc.matchAll(/\d{1,2}\/\d{1,2}\/\d{2,4}/g)];
    if (inner.length) {
      const last = inner[inner.length - 1];
      const after = desc.slice(last.index + last[0].length).trim();
      if (after.length >= 3) {
        desc = after;
        const dd = new Date(last[0]);
        if (!isNaN(dd))
          innerIso = `${dd.getFullYear()}-${String(dd.getMonth() + 1).padStart(2, "0")}-${String(dd.getDate()).padStart(2, "0")}`;
      }
    }
    desc = desc.slice(0, 60).trim();
    if (!desc) continue;
    if (amount < 0 && /payment|autopay|thank you/i.test(desc)) continue;   // card payments aren't charges
    const resp = await (await fetch("/api/txns", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ cardId: selectedId, date: innerIso || isoDate,
                             description: desc, amount, source: "sync" }),
    })).json();
    const entry = { date: innerIso || isoDate, desc, amount };
    if (resp.duplicate) { dupes++; skippedRows.push(entry); }
    else { added++; addedRows.push(entry); }
  }
  await refresh();

  // The receipt: exactly what came in, what was skipped, and whether the books now balance.
  const c = card();
  let verdict = "";
  if (c && c.stmtBalance != null) {
    const d = Math.round((c.stmtBalance - c.openTotal) * 100) / 100;
    verdict = Math.abs(d) < 0.01
      ? `<br><b style="color:var(--good)">✓ Balance check: open charges now match the bank exactly (${fmt(c.stmtBalance)}).</b>`
      : `<br><b style="color:var(--neg)">Balance check: still ${fmt(d)} off vs the bank's ${fmt(c.stmtBalance)}.</b>`;
  }
  const payLike = addedRows.filter(r => r.amount < 0).length;
  const box = $("#importMsg");
  box.innerHTML =
    `<b>Sync receipt — ${added} added, ${dupes} skipped as already in.</b>` +
    (addedRows.length ? `<br>Added:<br>${addedRows.slice(0, 30).map(r =>
      `&nbsp;&nbsp;${esc(r.date || "?")} · ${esc(r.desc.slice(0, 40))} · <b>${fmt(r.amount)}</b>`).join("<br>")}${addedRows.length > 30 ? `<br>…+${addedRows.length - 30} more` : ""}` : "") +
    (skippedRows.length ? `<br><span style="color:var(--muted)">Skipped (already have them): ${skippedRows.slice(0, 8).map(r => `${esc(r.desc.slice(0, 18))} ${fmt(r.amount)}`).join(" · ")}${skippedRows.length > 8 ? ` +${skippedRows.length - 8} more` : ""}</span>` : "") +
    (payLike ? `<br><span style="color:var(--warn)">⚠ ${payLike} negative row${payLike === 1 ? " looks" : "s look"} like card payments — use 🧹 Catch up to settle them with the charges they paid for.</span>` : "") +
    verdict;
  box.hidden = false;
  setTimeout(() => (box.hidden = true), 30000);
  toast(`Sync: ${added} added, ${dupes} skipped.${verdict ? (verdict.includes("✓") ? " ✓ Books match the bank." : " Balance still off — see the receipt.") : ""}`);
};

// ---------- notes ----------
$("#cvNote").oninput = (e) => { e.target.style.height = 'auto'; e.target.style.height = e.target.scrollHeight + 'px'; };
$("#cvNote").onchange = () => saveStrategy();
$("#stratEdit").onclick = async () => {
  strategyEdit = !strategyEdit;
  applyStrategyMode();
  if (strategyEdit) $("#cvNote").focus();
  else await saveStrategy();   // leaving edit mode commits any prose change
};

// ---------- import ----------
$("#fileInput").onchange = (e) => importFiles(e.target.files);
const panel = $("#panel");
panel.addEventListener("dragover", e => { e.preventDefault(); panel.classList.add("dragging"); });
panel.addEventListener("dragleave", () => panel.classList.remove("dragging"));
panel.addEventListener("drop", e => { e.preventDefault(); panel.classList.remove("dragging"); if (selectedId && e.dataTransfer.files.length) importFiles(e.dataTransfer.files); });

async function importFiles(files) {
  if (!selectedId || !files.length) return;
  const fd = new FormData();
  for (const f of files) fd.append("files", f);
  const res = await (await fetch(`/api/cards/${selectedId}/import`, { method: "POST", body: fd })).json();
  const box = $("#importMsg");
  box.innerHTML = importSummaryHtml(res);
  box.hidden = false;
  setTimeout(() => box.hidden = true, 15000);
  $("#fileInput").value = "";
  await refresh();
}

function importSummaryHtml(res) {
  return res.map(r => {
    if (r.savePoint) return `<b>${esc(r.file)}</b>: 💾 ${esc(r.error)}`;
    if (r.error) return `<b>${esc(r.file)}</b>: ⚠ ${esc(r.error)}`;
    let s = `<b>${esc(r.file)}</b> → ${esc(r.card)}: <b>${r.added} new</b>, ${r.duplicates} already in, ${r.needsReview} to review`;
    if (r.matchedPending) s += `, ${r.matchedPending} pending matched`;
    if (r.payments?.length) {
      const p = r.payments[r.payments.length - 1];
      s += `<br><span class="pay">💳 payment found: ${fmt(p.amount)} on ${p.date || "?"} — it'll be offered at settle time</span>`;
    }
    if (r.archivedTo) s += `<br><span style="color:var(--muted)">📦 archived → ${esc(r.archivedTo.replace(/^.*[\\/]Archive[\\/]/, "Archive/"))}</span>`;
    return s;
  }).join("<br>");
}

let toastTimer;
function toast(html, ms = 10000) {
  const t = $("#toast");
  t.innerHTML = html;
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.hidden = true), ms);
}

// ---------- statement balance / payer ----------
$("#cvBalance").onchange = async (e) => {
  const v = e.target.value === "" ? null : +e.target.value;
  await fetch(`/api/cards/${selectedId}/balance`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ balance: v }) });
  await refresh(true);
};
$("#cvPayer").onchange = async (e) => {
  await fetch(`/api/cards/${selectedId}`, { method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ defaultPayer: e.target.value }) });
  await refresh(true);
};

// Keeps owner tags ("Aryn:11009") intact, normalizes everything else to plain digits.
function normLast4(v) {
  return v.split(/[\s,;]+/).map(t => {
    const tagged = t.match(/^([A-Za-z]+)[:\-](\d{4,6})$/);
    if (tagged) return `${tagged[1][0].toUpperCase()}${tagged[1].slice(1).toLowerCase()}:${tagged[2]}`;
    const d = t.match(/\d{4,6}/);
    return d ? d[0] : null;
  }).filter(Boolean).join(" ");
}

$("#cvLast4").onchange = async (e) => {
  const norm = normLast4(e.target.value);
  e.target.value = norm;
  await fetch(`/api/cards/${selectedId}`, { method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ last4: norm }) });
  toast(norm
    ? `Got it — files with these numbers route to ${esc(mask(card().name))}${/[A-Za-z]:/.test(norm) ? ", and tagged swipes default to their owner" : ""}.`
    : "Card digits cleared.");
  await refresh(true);
};

// ---------- amazon paste-match (A+ / M+) ----------
let amzWho = "Aryn";
function openAmazon(w) {
  if (!selectedId) return;
  amzWho = w;
  $("#amzTitle").textContent = `${whoName(w)}'s Amazon transactions`;
  $("#amzWhoLabel").textContent = whoName(w);
  $("#amzText").value = "";
  $("#amazonModal").hidden = false;
  $("#amzText").focus();
}
$("#amzAryn").onclick = () => openAmazon("Aryn");
$("#amzMel").onclick = () => openAmazon("Mel");
$("#amzCancel").onclick = () => $("#amazonModal").hidden = true;
$("#amzGo").onclick = async () => {
  const text = $("#amzText").value;
  $("#amazonModal").hidden = true;
  if (!text.trim()) return;
  const r = await (await fetch(`/api/cards/${selectedId}/amazon-match`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ who: amzWho, text }),
  })).json();
  let msg = `<b>${r.matched} charge${r.matched === 1 ? "" : "s"} matched → ${esc(whoName(amzWho))}</b>`;
  if (r.giftCard) msg += `, ${r.giftCard} gift-card order${r.giftCard === 1 ? "" : "s"} skipped (not on the card)`;
  if (r.unmatched?.length)
    msg += `<br>No open charge found for: ${r.unmatched.slice(0, 6).map(esc).join(" · ")}${r.unmatched.length > 6 ? ` +${r.unmatched.length - 6} more` : ""}<br><span style="opacity:.8">(usually already settled, still pending, or on a different card)</span>`;
  toast(msg, 14000);
  await refresh();
};

// ---------- catch up (bulk-settle old, already-paid charges) ----------
$("#catchupBtn").onclick = async () => {
  const c = card(); if (!c) return;
  $("#cuCard").textContent = mask(c.name);
  let def = new Date().toISOString().slice(0, 10), hint = "";
  try {
    const det = await (await fetch(`/api/cards/${selectedId}/payments-detected`)).json();
    if (det.length) {
      const p = { date: det[0].date ?? det[0].Date, amount: det[0].amount ?? det[0].Amount };
      if (p.date) {
        def = p.date;
        hint = `Most recent payment seen on this card: ${fmt(p.amount)} on ${p.date} — charges before that date were probably covered by it.`;
      }
    }
  } catch { /* no payments seen */ }
  $("#cuDate").value = def;
  $("#cuHint").textContent = hint;
  $("#catchupModal").hidden = false;
};
$("#cuCancel").onclick = () => $("#catchupModal").hidden = true;
$("#cuConfirm").onclick = async () => {
  const d = $("#cuDate").value;
  if (!d) return;
  await fetch(`/api/cards/${selectedId}/bulk-settle-before?date=${d}`, { method: "POST" });
  $("#catchupModal").hidden = true;
  toast(`Open charges before ${d} marked as settled — they're under the Settled filter and in history as a "Catch-up".`);
  await refresh();
  await loadHistory();
};

// ---------- manual add ----------
$("#addBtn").onclick = async () => {
  const desc = $("#addDesc").value.trim();
  const amount = +$("#addAmount").value;
  if (!desc || !amount) return;
  await fetch("/api/txns", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ cardId: selectedId, date: $("#addDate").value || null, description: desc, amount }) });
  $("#addDesc").value = ""; $("#addAmount").value = "";
  await refresh();
};

// ---------- settle ----------
$("#settleBtn").onclick = async () => {
  const c = card(); if (!c) return;
  $("#smCard").textContent = mask(c.name);
  $("#smCarryMelL").textContent = whoName("Mel", true);
  $("#smCarryArynL").textContent = whoName("Aryn", true);
  $("#smSummary").innerHTML = `Shared ${fmt(c.sharedTotal)} → ${fmt(c.sharedTotal / 2)} each.
    ${esc(whoName("Mel"))}-only ${fmt(c.melTotal)}, ${esc(whoName("Aryn"))}-only ${fmt(c.arynTotal)}${c.carryMel || c.carryAryn ? `, carryover M ${fmt(c.carryMel)} / A ${fmt(c.carryAryn)}` : ""}.<br>
    <b class="mel">${esc(whoName("Mel"))}'s part: ${fmt(c.melPart)}</b> &nbsp; <b class="aryn">${esc(whoName("Aryn"))}'s part: ${fmt(c.arynPart)}</b>`;
  const today = new Date();
  $("#smLabel").value = `${today.getMonth() + 1}${String(today.getDate()).padStart(2, "0")}`;
  $("#smPayments").innerHTML = "";
  addPayLine(c.defaultPayer === "Mel" ? "Mel" : "Aryn", "card payment", Math.max(c.openTotal, 0));
  if (c.defaultPayer !== "Both")
    // Your real flow: the payer requests the other's share. "Requested" counts as their
    // payment but stays flagged ⏳ in history until you confirm the money landed.
    addPayLine(c.defaultPayer === "Mel" ? "Aryn" : "Mel", "venmo requested",
      Math.max(c.defaultPayer === "Mel" ? c.arynPart : c.melPart, 0));
  $("#smCarryMel").value = 0; $("#smCarryAryn").value = 0; $("#smNote").value = "";
  $("#settleModal").hidden = false;

  // payments spotted in the statement imports → one-click fill
  const box = $("#smDetected");
  box.innerHTML = "";
  try {
    const det = await (await fetch(`/api/cards/${selectedId}/payments-detected`)).json();
    for (const raw of det.slice(0, 3)) {
      const p = { amount: raw.amount ?? raw.Amount, date: raw.date ?? raw.Date, who: raw.who ?? raw.Who };
      if (p.amount == null) continue;
      const chip = document.createElement("button");
      chip.className = "paychip";
      chip.type = "button";
      chip.textContent = `💳 ${p.who ? `${whoName(p.who, true)} paid` : "you paid"} ${fmt(p.amount)} on ${p.date || "?"} — use`;
      chip.onclick = () => {
        const a = document.querySelector("#smPayments .pamount");
        if (a) a.value = p.amount.toFixed(2);
      };
      box.appendChild(chip);
    }
  } catch { /* none detected */ }
};

function addPayLine(whoVal = "Aryn", method = "venmo", amount = 0) {
  const div = document.createElement("div");
  div.className = "payline";
  div.innerHTML = `
    <select class="pwho"><option value="Aryn">${esc(whoName("Aryn", true))}</option><option value="Mel">${esc(whoName("Mel", true))}</option><option value="Split">Split</option></select>
    <select class="pmethod" title="Venmo requested = counts as that person's payment, but flag it until the money actually lands">
      <option value="card payment">paid the card bill</option>
      <option value="venmo requested">venmo requested ⏳</option>
      <option value="venmo">venmo sent ✓</option>
      <option value="venmo received">venmo received ✓</option>
      <option value="bank">own bank account</option>
      <option value="other">other</option>
    </select>
    <input class="pamount" type="number" step="0.01" value="${amount.toFixed(2)}">
    <input class="pnote" type="text" placeholder="note (e.g. paid 6/11)">
    <button class="rowbtn" type="button">✕</button>`;
  div.querySelector(".pwho").value = whoVal;
  div.querySelector(".pmethod").value = method;
  div.querySelector(".rowbtn").onclick = () => div.remove();
  $("#smPayments").appendChild(div);
}
$("#smAddPay").onclick = () => addPayLine();
$("#smCancel").onclick = () => $("#settleModal").hidden = true;
$("#smConfirm").onclick = async () => {
  const payments = [...document.querySelectorAll("#smPayments .payline")].map(d => ({
    who: d.querySelector(".pwho").value,
    method: d.querySelector(".pmethod").value,
    amount: +d.querySelector(".pamount").value || 0,
    note: d.querySelector(".pnote").value,
  })).filter(p => p.amount !== 0 || p.note);
  await fetch(`/api/cards/${selectedId}/settle`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      label: $("#smLabel").value,
      payments: JSON.stringify(payments),
      note: $("#smNote").value,
      by: who,
      newCarryMel: +$("#smCarryMel").value || 0,
      newCarryAryn: +$("#smCarryAryn").value || 0,
    }),
  });
  $("#settleModal").hidden = true;
  await refresh();
  await loadHistory();
};

// ---------- history ----------
async function loadHistory() {
  if (!selectedId) return;
  const rows = await (await fetch(`/api/cards/${selectedId}/settlements`)).json();
  const el = $("#historyBody");
  el.innerHTML = rows.length ? "" : `<div class="hitem">None yet.</div>`;
  let newest = true;
  for (const s of rows) {
    const canUndo = newest; newest = false;
    let pays = [];
    try { pays = JSON.parse(s.payments || "[]"); } catch { }
    const div = document.createElement("div");
    div.className = "hitem";
    div.innerHTML = `<b>${esc(s.label || "")}</b> — settled ${(s.settledAt || "").slice(0, 10)} by ${esc(whoName(s.by === "seed" ? "the spreadsheet" : (s.by || "?"), true))}.
      ${canUndo && s.by !== "seed" ? `<button class="vconfirm undo" data-s="${s.id}" title="Reopen these charges and remove this settle-up (carryovers aren't auto-restored)">↩ undo</button>` : ""}
      Shared ${fmt(s.sharedTotal || 0)}, ${esc(whoName("Mel"))} part ${fmt(s.melPart || 0)}, ${esc(whoName("Aryn"))} part ${fmt(s.arynPart || 0)}.
      ${pays.length ? `<div class="pay">${pays.map((p, idx) => {
        const isReq = (p.method || "").includes("requested");
        const text = `${esc(whoName(p.who, true))} ${esc(p.method)} ${fmt(p.amount)}${p.note ? ` (${esc(mask(p.note))})` : ""}`;
        if (!isReq) return text;
        // Requests are assumed paid. They only turn into amber "look into this" suspects
        // when the card's balance check is currently off.
        const cOff = card() && card().discrepancy != null && Math.abs(card().discrepancy) >= 0.01;
        return (cOff
          ? `<span class="payreq" title="Balance is off and this request was never confirmed — prime suspect">⚠ ${text}</span>`
          : `<span title="Assumed paid — the ✓ marks it confirmed if you ever want certainty">${text}</span>`)
          + ` <button class="vconfirm" data-s="${s.id}" data-i="${idx}" title="Money arrived — mark it received">✓</button>`;
      }).join(" · ")}</div>` : ""}
      ${s.note ? `<div class="pay">📝 ${esc(mask(s.note))}</div>` : ""}`;
    div.querySelectorAll(".vconfirm:not(.undo)").forEach(btn => {
      btn.onclick = async (e) => {
        e.stopPropagation();
        await fetch(`/api/settlements/${btn.dataset.s}/confirm-payment`, {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ index: +btn.dataset.i }),
        });
        toast("Marked received ✓ — the ⏳ is cleared.");
        await loadHistory();
      };
    });
    const undo = div.querySelector(".vconfirm.undo");
    if (undo) undo.onclick = async (e) => {
      e.stopPropagation();
      if (!confirm(`Undo settle-up "${s.label || ""}"?\nIts charges reopen and the record is removed. Carryovers set during it are NOT auto-restored.`)) return;
      await fetch(`/api/settlements/${undo.dataset.s}`, { method: "DELETE" });
      toast("Settle-up undone — those charges are open again.");
      await refresh();
      await loadHistory();
    };
    el.appendChild(div);
  }
}

// ---------- utils ----------
function esc(s) { return String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c])); }
function ord(d) { return d + ({ 1: "st", 2: "nd", 3: "rd", 21: "st", 22: "nd", 23: "rd", 31: "st" }[d] || "th"); }

// ---------- receipts ----------
let receiptTarget = null;
$("#receiptFile").onchange = async (e) => {
  if (!receiptTarget || !e.target.files.length) return;
  const fd = new FormData();
  fd.append("file", e.target.files[0]);
  await fetch(`/api/txns/${receiptTarget}/receipt`, { method: "POST", body: fd });
  e.target.value = ""; receiptTarget = null;
  toast("Receipt attached 📷");
  await loadTxns();
};

// ---------- AI assistant (the ? bubble) ----------
let aiAvailable = false;
const aiMsgs = [];
$("#aiFab").onclick = () => {
  const p = $("#aiPanel");
  p.hidden = !p.hidden;
  if (!p.hidden) {
    if (!aiMsgs.length) aiRender();
    $("#aiInput").focus();
  }
};
$("#aiClose").onclick = () => $("#aiPanel").hidden = true;

function aiRender(thinking = false) {
  const el = $("#aiMsgs");
  el.innerHTML = aiMsgs.map(m =>
    `<div class="aimsg ${m.role}">${m.preview ? `<img class="aiimg" src="${m.preview}" alt="screenshot">` : ""}${esc(m.content)}</div>`).join("") +
    (aiMsgs.length === 0 ? `<div class="aimsg assistant">${aiAvailable
      ? "Hi! I can dig through the cards — try “why is BILT off?” or “find the $40 from March”."
      : "Add your Anthropic API key in ⚙ Settings to wake me up."}</div>` : "") +
    (thinking ? `<div class="aimsg assistant thinking"><span></span><span></span><span></span></div>` : "");
  el.scrollTop = el.scrollHeight;
}

// ---------- screenshot attachment ----------
let aiPendingImage = null;   // { data (base64, no prefix), type, preview (dataURL) }
function setAiImage(file) {
  if (!file || !file.type.startsWith("image/")) return;
  const reader = new FileReader();
  reader.onload = () => {
    const dataUrl = reader.result;
    aiPendingImage = { data: dataUrl.split(",")[1], type: file.type, preview: dataUrl };
    $("#aiAttach").hidden = false;
    $("#aiAttach").innerHTML =
      `<img src="${dataUrl}" alt="attached"><button title="Remove">✕</button>`;
    $("#aiAttach button").onclick = clearAiImage;
  };
  reader.readAsDataURL(file);
}
function clearAiImage() {
  aiPendingImage = null;
  $("#aiAttach").hidden = true;
  $("#aiAttach").innerHTML = "";
  $("#aiImgFile").value = "";
}
$("#aiImgBtn").onclick = () => $("#aiImgFile").click();
$("#aiImgFile").onchange = (e) => e.target.files[0] && setAiImage(e.target.files[0]);
$("#aiInput").addEventListener("paste", (e) => {
  const img = [...(e.clipboardData?.items || [])].find(i => i.type.startsWith("image/"));
  if (img) { e.preventDefault(); setAiImage(img.getAsFile()); }
});

async function aiSend() {
  const text = $("#aiInput").value.trim();
  if (!text && !aiPendingImage) return;
  $("#aiInput").value = "";
  const msg = { role: "user", content: text || "(screenshot attached)" };
  if (aiPendingImage) {
    msg.image = aiPendingImage.data;
    msg.imageType = aiPendingImage.type;
    msg.preview = aiPendingImage.preview;
    clearAiImage();
  }
  aiMsgs.push(msg);
  aiRender(true);
  try {
    // Trim the payload: drop display-only previews, and keep image data only on the LAST
    // message — old screenshots are huge base64 and rarely need re-analysis. Big token save.
    const payload = aiMsgs.map((m, i) => {
      const o = { role: m.role, content: m.content };
      if (m.image && i === aiMsgs.length - 1) { o.image = m.image; o.imageType = m.imageType; }
      return o;
    });
    const r = await (await fetch("/api/ai/chat", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ messages: payload }),
    })).json();
    aiMsgs.push({ role: "assistant", content: r.reply || "(no reply)" });
  } catch (e) {
    aiMsgs.push({ role: "assistant", content: "Request failed — is the app online? " + e.message });
  }
  aiRender();
  await refresh(true);   // the assistant may have changed data
}
$("#aiSend").onclick = aiSend;
$("#aiInput").onkeydown = (e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); aiSend(); } };
document.querySelectorAll(".ai-chips button").forEach(b => {
  b.onclick = () => { $("#aiInput").value = b.dataset.q; aiSend(); };
});

$("#insightsBtn").onclick = () => {
  $("#aiPanel").hidden = false;
  $("#aiInput").value = "Give me this month's insights: a short narrative of notable changes, " +
    "then a rewards optimization review — pull the spending stats, figure out which purchases " +
    "were on suboptimal cards (quantify the missed cashback in dollars), and check whether our " +
    "recent spend volume would have satisfied any current signup-bonus minimums worth grabbing.";
  aiSend();
};

// ---------- boot ----------
renderWho();
loadBlurTerms().then(() => refresh()).then(() => { if (selectedId) selectCard(selectedId); });
setInterval(() => refresh(true), 30000);
