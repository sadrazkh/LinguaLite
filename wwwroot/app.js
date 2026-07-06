const tg = window.Telegram?.WebApp;
tg?.ready();
tg?.expand();

const storage = {
  apiKey: "lingualite.openrouterApiKey",
  adminToken: "lingualite.adminToken",
  devUserId: "lingualite.devUserId"
};

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
  frontCaption: $("#frontCaption"),
  backCaption: $("#backCaption"),
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
  aiPanel: $("#aiPanel"),
  aiPanelTitle: $("#aiPanelTitle"),
  aiPanelHint: $("#aiPanelHint"),
  aiWordInput: $("#aiWordInput"),
  completeAiButton: $("#completeAiButton"),
  typeHelper: $("#typeHelper"),
  feedbackGuide: $("#feedbackGuide"),
  settingsForm: $("#settingsForm"),
  apiKeyInput: $("#apiKeyInput"),
  redeemCodeInput: $("#redeemCodeInput"),
  redeemCodeButton: $("#redeemCodeButton"),
  exportButton: $("#exportButton"),
  importFileInput: $("#importFileInput"),
  frontLabel: $("#frontLabel"),
  backLabel: $("#backLabel"),
  exampleLabel: $("#exampleLabel"),
  promptLabel: $("#promptLabel"),
  answerLabel: $("#answerLabel"),
  notesLabel: $("#notesLabel"),
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

const typeCopy = {
  Word: {
    title: "تکمیل لغت با OpenRouter",
    helper: "برای لغت، AI معنی فارسی، مثال طبیعی، سوال مرور و نکته کاربردی می‌سازد.",
    aiHint: "یک کلمه یا عبارت کوتاه بنویس؛ بقیه فیلدهای کارت پر می‌شود.",
    aiPlaceholder: "مثلا: ancestor",
    front: "لغت / عبارت",
    back: "معنی و توضیح فارسی",
    example: "مثال انگلیسی",
    prompt: "سوال یادآوری",
    answer: "جواب کوتاه",
    notes: "نکته کاربردی",
    placeholders: ["ancestor", "جد، نیا", "My ancestors lived near the sea.", "What does ancestor mean?", "جد یا نیا", "معمولا درباره خانواده و نسل‌های قبلی استفاده می‌شود."]
  },
  Sentence: {
    title: "تکمیل جمله با OpenRouter",
    helper: "برای جمله، AI معنی، ساختار و تمرین بازسازی جمله را آماده می‌کند.",
    aiHint: "یک جمله انگلیسی بده تا کارت جمله‌محور بسازد.",
    aiPlaceholder: "مثلا: I have been learning English for two years.",
    front: "جمله",
    back: "معنی و نکته ساختاری",
    example: "مثال مشابه",
    prompt: "تمرین بازسازی",
    answer: "جواب درست",
    notes: "نکته گرامری",
    placeholders: ["I have been learning English.", "من دارم انگلیسی یاد می‌گیرم.", "She has been working all day.", "Translate: من دو سال است انگلیسی می‌خوانم.", "I have been learning English for two years.", "برای کاری که از گذشته شروع شده و هنوز ادامه دارد."]
  },
  Question: {
    title: "ساخت کارت پرسشی با OpenRouter",
    helper: "برای پرسش، AI سوال، جواب نمونه و نکته مکالمه‌ای می‌سازد.",
    aiHint: "موضوع یا سوال را بنویس تا کارت پرسش و پاسخ آماده شود.",
    aiPlaceholder: "مثلا: how to ask about someone's job",
    front: "سوال",
    back: "توضیح فارسی",
    example: "نمونه مکالمه",
    prompt: "پرسش تمرینی",
    answer: "جواب نمونه",
    notes: "نکته مکالمه",
    placeholders: ["What do you do?", "برای پرسیدن شغل طرف مقابل.", "A: What do you do? B: I am a programmer.", "Answer naturally: What do you do?", "I am a programmer.", "در مکالمه روزمره از What is your job? طبیعی‌تر است."]
  },
  Feedback: {
    title: "ساخت کارت فیدبک با OpenRouter",
    helper: "فیدبک یعنی اشتباه واقعی خودت را تبدیل به کارت مرور کنی؛ دستور AI اینجا مخصوص اصلاح اشتباه است.",
    aiHint: "اشتباه، اصلاح استاد یا جمله غلط را بنویس؛ AI دلیل، الگو و تمرین اصلاح می‌سازد.",
    aiPlaceholder: "مثلا: I programmer / استاد گفت: I am a programmer",
    front: "اشتباه → اصلاح درست",
    back: "دلیل اصلاح به فارسی",
    example: "مثال درست",
    prompt: "تمرین اصلاح",
    answer: "جواب صحیح",
    notes: "الگو و هشدار",
    placeholders: ["wrong: I programmer → correct: I am a programmer", "در جمله انگلیسی برای معرفی شغل باید فعل be و حرف تعریف a بیاید.", "I am a programmer.", "Correct this sentence: I programmer.", "I am a programmer.", "الگو: I am a/an + job. نگوییم I programmer."]
  }
};

applyTelegramTheme();
bindEvents();
loadSettings();
updateCardMode();
loadAll();

function bindEvents() {
  document.querySelectorAll(".tab").forEach((tab) => {
    tab.addEventListener("click", () => switchView(tab.dataset.view));
  });

  document.querySelectorAll('input[name="type"]').forEach((radio) => {
    radio.addEventListener("change", updateCardMode);
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

  const buttonColor = tg?.themeParams?.button_color;
  const buttonTextColor = tg?.themeParams?.button_text_color;
  if (buttonColor) root.style.setProperty("--primary", buttonColor);
  if (buttonTextColor) root.style.setProperty("--primary-text", buttonTextColor);
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
  const isFeedback = card.type === "Feedback";
  elements.reviewCard.hidden = false;
  elements.emptyState.hidden = true;
  elements.reviewCard.classList.toggle("feedback-review", isFeedback);
  elements.cardType.textContent = typeLabels[card.type] ?? "کارت";
  elements.boxLabel.textContent = `جعبه ${toPersianNumber(card.box)}`;
  elements.frontCaption.textContent = isFeedback ? "اشتباه و اصلاح" : "روی کارت";
  elements.backCaption.textContent = isFeedback ? "دلیل و الگو" : "پشت کارت";
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
    updateCardMode();
    showToast("کارت اضافه شد.");
    await loadAll();
    switchView("review");
  } catch (error) {
    showToast(error.message || "کارت ذخیره نشد.");
  }
}

async function completeWithAi() {
  const type = getSelectedType();
  const text = elements.aiWordInput.value.trim() || elements.frontInput.value.trim();
  if (!text) {
    showToast(type === "Feedback" ? "اول اشتباه یا فیدبک استاد را بنویس." : "اول متن کارت را وارد کن.");
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
    showToast(type === "Feedback" ? "کارت فیدبک آماده شد." : "فیلدهای کارت پر شد.");
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
  updateCardMode();
}

function updateCardMode() {
  const type = getSelectedType();
  const copy = typeCopy[type] || typeCopy.Word;
  const placeholders = copy.placeholders;
  const isFeedback = type === "Feedback";

  document.body.classList.toggle("feedback-mode", isFeedback);
  elements.aiPanelTitle.textContent = copy.title;
  elements.typeHelper.textContent = copy.helper;
  elements.aiPanelHint.textContent = copy.aiHint;
  elements.aiWordInput.placeholder = copy.aiPlaceholder;
  elements.feedbackGuide.hidden = !isFeedback;
  elements.frontLabel.textContent = copy.front;
  elements.backLabel.textContent = copy.back;
  elements.exampleLabel.textContent = copy.example;
  elements.promptLabel.textContent = copy.prompt;
  elements.answerLabel.textContent = copy.answer;
  elements.notesLabel.textContent = copy.notes;
  elements.frontInput.placeholder = placeholders[0];
  elements.backInput.placeholder = placeholders[1];
  elements.exampleInput.placeholder = placeholders[2];
  elements.promptInput.placeholder = placeholders[3];
  elements.answerInput.placeholder = placeholders[4];
  elements.notesInput.placeholder = placeholders[5];
}

function getSelectedType() {
  return document.querySelector('input[name="type"]:checked')?.value || "Word";
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
      <div>
        <strong>${escapeHtml(user.displayName || user.id)}</strong>
        <small>${escapeHtml(user.id)} · ${escapeHtml(user.plan)} · ${user.isActive ? "فعال" : "غیرفعال"}</small>
      </div>
      <button class="mini-button" type="button" data-user-id="${escapeHtml(user.id)}" data-active="${user.isActive}">
        ${user.isActive ? "غیرفعال کن" : "فعال کن"}
      </button>
    </article>
  `).join("");

  elements.usersList.querySelectorAll(".mini-button").forEach((button) => {
    button.addEventListener("click", () => toggleUserActive(button.dataset.userId, button.dataset.active !== "true"));
  });
}

async function toggleUserActive(id, isActive) {
  try {
    await adminFetch(`/api/admin/users/${encodeURIComponent(id)}`, {
      method: "PUT",
      body: JSON.stringify({ isActive })
    });
    await loadAdmin();
  } catch (error) {
    showToast(error.message || "وضعیت کاربر تغییر نکرد.");
  }
}

function renderCodes(codes) {
  elements.codesList.innerHTML = codes.map((code) => `
    <article class="admin-item">
      <div>
        <strong>${escapeHtml(code.code)}</strong>
        <small>${escapeHtml(code.plan)} · ${toPersianNumber(code.uses)}/${toPersianNumber(code.maxUses)}</small>
      </div>
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
    <article class="deck-item ${card.type === "Feedback" ? "feedback-item" : ""}">
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
