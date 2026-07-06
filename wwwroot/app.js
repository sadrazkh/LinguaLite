const tg = window.Telegram?.WebApp;
tg?.ready();
tg?.expand();

const storage = {
  apiKey: "lingualite.openrouterApiKey",
  adminToken: "lingualite.adminToken",
  devUserId: "lingualite.devUserId"
};

const defaultPrompt = `Feedback cards:
Input: "I programmer"
Output should teach: "I am a programmer"
Explain why "am" and article "a" are needed.`;

const devUserId = getOrCreateDevUserId();
const state = { due: [], current: null, config: null };

const $ = (selector) => document.querySelector(selector);
const elements = {
  profileText: $("#profileText"),
  modelText: $("#modelText"),
  dueCards: $("#dueCards"),
  totalCards: $("#totalCards"),
  accuracy: $("#accuracy"),
  cardType: $("#cardType"),
  boxLabel: $("#boxLabel"),
  boxProgress: $("#boxProgress"),
  frontText: $("#frontText"),
  exampleText: $("#exampleText"),
  backText: $("#backText"),
  qaText: $("#qaText"),
  answerPanel: $("#answerPanel"),
  reviewCard: $("#reviewCard"),
  emptyState: $("#emptyState"),
  revealButton: $("#revealButton"),
  rememberedButton: $("#rememberedButton"),
  forgotButton: $("#forgotButton"),
  refreshButton: $("#refreshButton"),
  form: $("#cardForm"),
  boxes: $("#boxes"),
  deckList: $("#deckList"),
  toast: $("#toast"),
  aiWordInput: $("#aiWordInput"),
  completeAiButton: $("#completeAiButton"),
  settingsForm: $("#settingsForm"),
  apiKeyInput: $("#apiKeyInput"),
  redeemCodeInput: $("#redeemCodeInput"),
  redeemCodeButton: $("#redeemCodeButton"),
  exportButton: $("#exportButton"),
  importFileInput: $("#importFileInput"),
  frontInput: $("#frontInput"),
  backInput: $("#backInput"),
  exampleInput: $("#exampleInput"),
  promptInput: $("#promptInput"),
  answerInput: $("#answerInput"),
  notesInput: $("#notesInput"),
  adminLoginForm: $("#adminLoginForm"),
  adminTokenInput: $("#adminTokenInput"),
  adminPanel: $("#adminPanel"),
  createCodeButton: $("#createCodeButton"),
  codePlanInput: $("#codePlanInput"),
  codeMaxUsesInput: $("#codeMaxUsesInput"),
  usersList: $("#usersList"),
  codesList: $("#codesList")
};

const typeLabels = {
  Word: "لغت",
  Sentence: "جمله",
  Question: "پرسش",
  Feedback: "فیدبک"
};

applyTelegramTheme();
bindEvents();
loadSettings();
loadAll();

function bindEvents() {
  document.querySelectorAll(".tab").forEach((tab) => {
    tab.addEventListener("click", () => switchView(tab.dataset.view));
  });

  elements.revealButton.addEventListener("click", () => {
    elements.answerPanel.hidden = false;
    elements.revealButton.hidden = true;
    tg?.HapticFeedback?.impactOccurred("light");
  });

  elements.rememberedButton.addEventListener("click", () => reviewCurrent(true));
  elements.forgotButton.addEventListener("click", () => reviewCurrent(false));
  elements.refreshButton.addEventListener("click", loadAll);
  elements.form.addEventListener("submit", createCard);
  elements.completeAiButton.addEventListener("click", completeWithAi);
  elements.settingsForm.addEventListener("submit", saveSettings);
  elements.redeemCodeButton.addEventListener("click", redeemCode);
  elements.exportButton.addEventListener("click", exportDeck);
  elements.importFileInput.addEventListener("change", importDeck);
  elements.adminLoginForm.addEventListener("submit", adminLogin);
  elements.createCodeButton.addEventListener("click", createAccessCode);
}

function applyTelegramTheme() {
  const root = document.documentElement;
  const colorScheme = tg?.colorScheme || (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
  root.dataset.theme = colorScheme === "dark" ? "dark" : "light";

  const theme = tg?.themeParams;
  if (!theme) return;
  if (theme.bg_color) root.style.setProperty("--bg", theme.bg_color);
  if (theme.secondary_bg_color) root.style.setProperty("--surface", theme.secondary_bg_color);
  if (theme.section_bg_color) root.style.setProperty("--surface-2", theme.section_bg_color);
  if (theme.text_color) root.style.setProperty("--text", theme.text_color);
  if (theme.hint_color) root.style.setProperty("--muted", theme.hint_color);
  if (theme.button_color) root.style.setProperty("--primary", theme.button_color);
  if (theme.button_text_color) root.style.setProperty("--primary-text", theme.button_text_color);
}

async function loadAll() {
  try {
    const [config, summary, due, cards] = await Promise.all([
      fetchJson("/api/config"),
      fetchJson("/api/deck"),
      fetchJson("/api/cards/due"),
      fetchJson("/api/cards")
    ]);

    state.config = config;
    state.due = due;
    renderProfile(config);
    updateSummary(summary);
    renderBoxes(summary.boxes);
    renderDeck(cards);
    pickNextCard();
  } catch (error) {
    showToast(error.message || "دریافت اطلاعات انجام نشد.");
  }
}

function renderProfile(config) {
  elements.profileText.textContent = `${config.displayName || config.userId} · ${config.plan}`;
  elements.modelText.textContent = `مدل سرور: ${config.openRouterModel}`;
}

function switchView(viewName) {
  document.querySelectorAll(".tab").forEach((tab) => {
    tab.classList.toggle("active", tab.dataset.view === viewName);
  });
  document.querySelectorAll(".view").forEach((view) => {
    view.classList.toggle("active", view.id === `${viewName}View`);
  });
  if (viewName === "deck") loadAll();
}

function updateSummary(summary) {
  elements.dueCards.textContent = toPersianNumber(summary.dueCards);
  elements.totalCards.textContent = toPersianNumber(summary.totalCards);
  elements.accuracy.textContent = `${toPersianNumber(summary.accuracy)}%`;
}

function pickNextCard() {
  state.current = state.due.shift() ?? null;
  elements.answerPanel.hidden = true;
  elements.revealButton.hidden = false;

  if (!state.current) {
    elements.reviewCard.hidden = true;
    elements.emptyState.hidden = false;
    setBoxProgress(0);
    return;
  }

  const card = state.current;
  elements.reviewCard.hidden = false;
  elements.emptyState.hidden = true;
  elements.cardType.textContent = typeLabels[card.type] ?? "کارت";
  elements.boxLabel.textContent = `جعبه ${toPersianNumber(card.box)}`;
  elements.frontText.textContent = card.front;
  elements.exampleText.textContent = card.example || card.prompt || "";
  elements.backText.textContent = card.back;
  elements.qaText.textContent = [card.prompt, card.answer, card.notes].filter(Boolean).join(" · ");
  setBoxProgress(card.box);
}

async function reviewCurrent(remembered) {
  if (!state.current) return;
  try {
    await fetchJson(`/api/cards/${state.current.id}/review`, {
      method: "POST",
      body: JSON.stringify({ remembered })
    });
    showToast(remembered ? "رفت جعبه بعدی." : "برای مرور دوباره برگشت.");
    await loadAll();
  } catch (error) {
    showToast(error.message || "ثبت مرور انجام نشد.");
  }
}

async function createCard(event) {
  event.preventDefault();
  const payload = Object.fromEntries(new FormData(elements.form).entries());
  try {
    await fetchJson("/api/cards", { method: "POST", body: JSON.stringify(payload) });
    elements.form.reset();
    showToast("کارت اضافه شد.");
    await loadAll();
    switchView("review");
  } catch (error) {
    showToast(error.message || "کارت ذخیره نشد.");
  }
}

async function completeWithAi() {
  const text = elements.aiWordInput.value.trim() || elements.frontInput.value.trim();
  const type = document.querySelector('input[name="type"]:checked')?.value || "Word";
  if (!text) {
    showToast("اول متن کارت یا فیدبک را وارد کن.");
    return;
  }

  elements.completeAiButton.disabled = true;
  elements.completeAiButton.textContent = "در حال تکمیل...";
  try {
    const apiKey = localStorage.getItem(storage.apiKey) || "";
    const headers = apiKey ? { "X-OpenRouter-Api-Key": apiKey } : {};
    const card = await fetchJson("/api/ai/complete", {
      method: "POST",
      headers,
      body: JSON.stringify({ text, type })
    });
    fillCardForm(card);
    showToast("فیلدهای کارت پر شد.");
  } catch (error) {
    showToast(error.message || "تکمیل با OpenRouter انجام نشد.");
  } finally {
    elements.completeAiButton.disabled = false;
    elements.completeAiButton.textContent = "پر کن";
  }
}

function fillCardForm(card) {
  elements.frontInput.value = card.front || "";
  elements.backInput.value = card.back || "";
  elements.exampleInput.value = card.example || "";
  elements.promptInput.value = card.prompt || "";
  elements.answerInput.value = card.answer || "";
  elements.notesInput.value = card.notes || "";
  const radio = document.querySelector(`input[name="type"][value="${card.type || "Word"}"]`);
  if (radio) radio.checked = true;
}

function loadSettings() {
  elements.apiKeyInput.value = localStorage.getItem(storage.apiKey) || "";
  elements.adminTokenInput.value = localStorage.getItem(storage.adminToken) || "";
}

function saveSettings(event) {
  event.preventDefault();
  localStorage.setItem(storage.apiKey, elements.apiKeyInput.value.trim());
  showToast("تنظیمات ذخیره شد.");
}

async function redeemCode() {
  try {
    await fetchJson("/api/access/redeem", {
      method: "POST",
      body: JSON.stringify({ code: elements.redeemCodeInput.value.trim() })
    });
    showToast("کد فعال شد.");
    await loadAll();
  } catch (error) {
    showToast(error.message || "کد فعال نشد.");
  }
}

async function exportDeck() {
  try {
    const data = await fetchJson("/api/export");
    const blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `lingualite-export-${Date.now()}.json`;
    link.click();
    URL.revokeObjectURL(url);
  } catch (error) {
    showToast(error.message || "خروجی گرفتن انجام نشد.");
  }
}

async function importDeck(event) {
  const file = event.target.files?.[0];
  if (!file) return;
  try {
    const payload = JSON.parse(await file.text());
    await fetchJson("/api/import", {
      method: "POST",
      body: JSON.stringify({ cards: payload.cards || [], mode: "Merge" })
    });
    showToast("کارت‌ها ایمپورت شدند.");
    await loadAll();
  } catch (error) {
    showToast(error.message || "ایمپورت انجام نشد.");
  } finally {
    event.target.value = "";
  }
}

async function adminLogin(event) {
  event.preventDefault();
  localStorage.setItem(storage.adminToken, elements.adminTokenInput.value.trim());
  elements.adminPanel.hidden = false;
  await loadAdmin();
}

async function loadAdmin() {
  try {
    const [users, codes] = await Promise.all([
      adminFetch("/api/admin/users"),
      adminFetch("/api/admin/codes")
    ]);
    renderUsers(users);
    renderCodes(codes);
  } catch (error) {
    showToast(error.message || "ورود ادمین ناموفق بود.");
  }
}

async function createAccessCode() {
  try {
    const code = await adminFetch("/api/admin/codes", {
      method: "POST",
      body: JSON.stringify({
        plan: elements.codePlanInput.value.trim() || "Free",
        maxUses: Number(elements.codeMaxUsesInput.value || 1),
        features: { ai: true, exportImport: true, feedbackCards: true, unlimitedCards: true }
      })
    });
    showToast(`کد ساخته شد: ${code.code}`);
    await loadAdmin();
  } catch (error) {
    showToast(error.message || "کد ساخته نشد.");
  }
}

function renderUsers(users) {
  elements.usersList.innerHTML = users.map((user) => `
    <article class="admin-item">
      <strong>${escapeHtml(user.displayName || user.id)}</strong>
      <small>${escapeHtml(user.id)} · ${escapeHtml(user.plan)} · ${user.isActive ? "فعال" : "غیرفعال"}</small>
    </article>
  `).join("");
}

function renderCodes(codes) {
  elements.codesList.innerHTML = codes.map((code) => `
    <article class="admin-item">
      <strong>${escapeHtml(code.code)}</strong>
      <small>${escapeHtml(code.plan)} · ${toPersianNumber(code.uses)}/${toPersianNumber(code.maxUses)}</small>
    </article>
  `).join("");
}

async function adminFetch(url, options = {}) {
  const headers = new Headers(options.headers || {});
  headers.set("Content-Type", "application/json");
  headers.set("X-Admin-Token", localStorage.getItem(storage.adminToken) || "");
  const response = await fetch(url, { ...options, headers });
  if (!response.ok) throw new Error("دسترسی ادمین تایید نشد.");
  return response.status === 204 ? null : response.json();
}

function setBoxProgress(box) {
  elements.boxProgress.querySelectorAll("i").forEach((item, index) => item.classList.toggle("active", index < box));
}

function renderBoxes(boxes) {
  elements.boxes.innerHTML = "";
  for (let box = 1; box <= 5; box += 1) {
    const count = boxes[box] ?? boxes[String(box)] ?? 0;
    const node = document.createElement("div");
    node.className = "box-pill";
    node.innerHTML = `<span>${toPersianNumber(count)}</span><small>جعبه ${toPersianNumber(box)}</small>`;
    elements.boxes.appendChild(node);
  }
}

function renderDeck(cards) {
  elements.deckList.innerHTML = cards.length ? cards.map((card) => `
    <article class="deck-item">
      <h3>${escapeHtml(card.front)}</h3>
      <p>${escapeHtml(card.back)}</p>
      <footer><span>${typeLabels[card.type] ?? "کارت"}</span><span>جعبه ${toPersianNumber(card.box)}</span></footer>
    </article>
  `).join("") : `<div class="empty-state"><strong>هنوز کارتی نداری.</strong><span>از بخش افزودن شروع کن.</span></div>`;
}

async function fetchJson(url, options = {}) {
  const headers = new Headers(options.headers || {});
  headers.set("Content-Type", "application/json");
  headers.set("X-Dev-User-Id", devUserId);
  if (tg?.initData) headers.set("X-Telegram-Init-Data", tg.initData);

  const response = await fetch(url, { ...options, headers });
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    throw new Error(error.message || "درخواست ناموفق بود.");
  }
  return response.status === 204 ? null : response.json();
}

function getOrCreateDevUserId() {
  const existing = localStorage.getItem(storage.devUserId);
  if (existing) return existing;
  const created = crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`;
  localStorage.setItem(storage.devUserId, created);
  return created;
}

function showToast(message) {
  elements.toast.textContent = message;
  elements.toast.classList.add("show");
  clearTimeout(showToast.timer);
  showToast.timer = setTimeout(() => elements.toast.classList.remove("show"), 2600);
}

function toPersianNumber(value) {
  return String(value).replace(/\d/g, (digit) => "۰۱۲۳۴۵۶۷۸۹"[digit]);
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}
