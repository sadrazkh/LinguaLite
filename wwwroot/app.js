const tg = window.Telegram?.WebApp;
tg?.ready();
tg?.expand();

const storage = {
  apiKey: "lingualite.openrouterApiKey",
  devUserId: "lingualite.devUserId",
  sessionToken: "lingualite.sessionToken",
  theme: "lingualite.theme"
};

const devUserId = getOrCreateDevUserId();
const state = {
  due: [],
  cards: [],
  current: null,
  config: null,
  editingCardId: null,
  dictionary: null,
  correction: null
};

const $ = (selector) => document.querySelector(selector);
const elements = {
  profileText: $("#profileText"),
  planBadge: $("#planBadge"),
  accountStatusText: $("#accountStatusText"),
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
  authGate: $("#authGate"),
  openBotButton: $("#openBotButton"),
  browserLoginForm: $("#browserLoginForm"),
  browserLoginCodeInput: $("#browserLoginCodeInput"),
  browserLoginButton: $("#browserLoginButton"),
  authHelpText: $("#authHelpText"),
  form: $("#cardForm"),
  formTitle: $("#formTitle"),
  saveCardButton: $("#saveCardButton"),
  cancelEditButton: $("#cancelEditButton"),
  boxes: $("#boxes"),
  deckList: $("#deckList"),
  toast: $("#toast"),
  aiPanel: $("#aiPanel"),
  aiPanelTitle: $("#aiPanelTitle"),
  aiPanelHint: $("#aiPanelHint"),
  aiWordInput: $("#aiWordInput"),
  completeAiButton: $("#completeAiButton"),
  cardQuotaText: $("#cardQuotaText"),
  typeHelper: $("#typeHelper"),
  feedbackGuide: $("#feedbackGuide"),
  dictionaryInput: $("#dictionaryInput"),
  dictionaryButton: $("#dictionaryButton"),
  dictionaryQuotaText: $("#dictionaryQuotaText"),
  dictionaryResult: $("#dictionaryResult"),
  correctionInput: $("#correctionInput"),
  correctionButton: $("#correctionButton"),
  correctionQuotaText: $("#correctionQuotaText"),
  correctionResult: $("#correctionResult"),
  apiKeyForm: $("#apiKeyForm"),
  activationForm: $("#activationForm"),
  apiKeyInput: $("#apiKeyInput"),
  redeemCodeInput: $("#redeemCodeInput"),
  redeemCodeButton: $("#redeemCodeButton"),
  dailyReportText: $("#dailyReportText"),
  usageGrid: $("#usageGrid"),
  languageLevelInput: $("#languageLevelInput"),
  savePreferencesButton: $("#savePreferencesButton"),
  checkUpdateButton: $("#checkUpdateButton"),
  themeSelector: $("#themeSelector"),
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
  userIdText: $("#userIdText"),
  userPlanText: $("#userPlanText"),
  storageProviderText: $("#storageProviderText"),
  userFeaturesText: $("#userFeaturesText")
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
    helper: "برای لغت، معنی فارسی، مثال طبیعی، سؤال مرور و نکته کاربردی ساخته می‌شود.",
    aiHint: "یک کلمه یا عبارت کوتاه بنویس؛ بقیه فیلدهای کارت پر می‌شود.",
    aiPlaceholder: "ancestor",
    front: "لغت / عبارت",
    back: "معنی و توضیح فارسی",
    example: "مثال انگلیسی",
    prompt: "سؤال یادآوری",
    answer: "جواب کوتاه",
    notes: "نکته کاربردی",
    placeholders: ["ancestor", "جد، نیا", "My ancestors lived near the sea.", "What does ancestor mean?", "جد یا نیا", "معمولا درباره خانواده و نسل‌های قبلی استفاده می‌شود."]
  },
  Sentence: {
    title: "تکمیل جمله با OpenRouter",
    helper: "برای جمله، معنی، ساختار و تمرین بازسازی جمله آماده می‌شود.",
    aiHint: "یک جمله انگلیسی بده تا کارت جمله‌محور ساخته شود.",
    aiPlaceholder: "I have been learning English for two years.",
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
    helper: "برای پرسش، جواب نمونه و نکته مکالمه‌ای ساخته می‌شود.",
    aiHint: "موضوع یا سؤال را بنویس تا کارت پرسش و پاسخ آماده شود.",
    aiPlaceholder: "how to ask about someone's job",
    front: "سؤال",
    back: "توضیح فارسی",
    example: "نمونه مکالمه",
    prompt: "پرسش تمرینی",
    answer: "جواب نمونه",
    notes: "نکته مکالمه",
    placeholders: ["What do you do?", "برای پرسیدن شغل طرف مقابل.", "A: What do you do? B: I am a programmer.", "Answer naturally: What do you do?", "I am a programmer.", "در مکالمه روزمره از What is your job? طبیعی‌تر است."]
  },
  Feedback: {
    title: "ساخت کارت فیدبک با OpenRouter",
    helper: "اشتباه واقعی خودت را تبدیل به کارت مرور کن؛ روی کارت فقط تمرین اصلاح را می‌بینی.",
    aiHint: "اشتباه، اصلاح استاد یا جمله غلط را بنویس؛ AI دلیل، الگو و تمرین اصلاح می‌سازد.",
    aiPlaceholder: "I programmer / استاد گفت: I am a programmer",
    front: "متن فیدبک یا اشتباه",
    back: "توضیح تکمیلی",
    example: "مثال درست",
    prompt: "تمرین اصلاح",
    answer: "جواب صحیح",
    notes: "الگو و هشدار",
    placeholders: ["I programmer / استاد گفت: I am a programmer", "اگر توضیحی از استاد داری اینجا بنویس.", "I am a programmer.", "Correct this sentence: I programmer.", "I am a programmer.", "الگو: I am a/an + job. نگوییم I programmer."]
  }
};

applyTelegramTheme();
bindEvents();
loadSettings();
registerPwa();
updateCardMode();
loadAll();

function bindEvents() {
  document.querySelectorAll(".tab").forEach((tab) => {
    tab.addEventListener("click", () => switchView(tab.dataset.view));
  });

  document.querySelectorAll('input[name="type"]').forEach((radio) => {
    radio.addEventListener("change", updateCardMode);
  });

  elements.revealButton.addEventListener("click", revealCurrentCard);
  elements.rememberedButton.addEventListener("click", () => reviewCurrent(true));
  elements.forgotButton.addEventListener("click", () => reviewCurrent(false));
  elements.refreshButton.addEventListener("click", loadAll);
  elements.browserLoginForm.addEventListener("submit", browserLogin);
  elements.form.addEventListener("submit", saveCard);
  elements.cancelEditButton.addEventListener("click", resetCardForm);
  elements.completeAiButton.addEventListener("click", completeWithAi);
  elements.dictionaryButton.addEventListener("click", lookupDictionary);
  elements.correctionButton.addEventListener("click", correctText);
  elements.apiKeyForm.addEventListener("submit", saveSettings);
  elements.activationForm.addEventListener("submit", redeemCode);
  elements.savePreferencesButton.addEventListener("click", savePreferences);
  elements.checkUpdateButton.addEventListener("click", checkForAppUpdate);
  document.querySelectorAll('input[name="theme"]').forEach((radio) => {
    radio.addEventListener("change", () => setThemeMode(radio.value));
  });
  elements.exportButton.addEventListener("click", exportDeck);
  elements.importFileInput.addEventListener("change", importDeck);
}

function applyTelegramTheme() {
  const mode = localStorage.getItem(storage.theme) || "auto";
  const selected = document.querySelector(`input[name="theme"][value="${mode}"]`);
  if (selected) selected.checked = true;
  applyThemeMode(mode);
}

function setThemeMode(mode) {
  localStorage.setItem(storage.theme, mode);
  applyThemeMode(mode);
}

function applyThemeMode(mode) {
  const root = document.documentElement;
  const colorScheme = mode === "auto"
    ? (tg?.colorScheme || (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light"))
    : mode;
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
    document.body.classList.remove("auth-required");
    elements.authGate.hidden = true;
    state.due = due;
    state.cards = cards;
    renderProfile(config);
    renderUserInfo(config);
    updateSummary(summary);
    renderQuota(config);
    renderBoxes(summary.boxes);
    renderDeck(cards);
    pickNextCard();
  } catch (error) {
    if (error.status === 401) {
      await showAuthGate();
      return;
    }
    showLoadError(error);
    showToast(error.message || "دریافت اطلاعات انجام نشد.");
  }
}

async function showAuthGate() {
  document.body.classList.add("auth-required");
  elements.authGate.hidden = false;
  elements.accountStatusText.textContent = "نیاز به ورود";
  try {
    const publicSettings = await fetchJson("/api/public-settings", { skipAuth: true });
    const username = publicSettings.telegramBotUsername;
    if (username) {
      elements.openBotButton.href = `https://t.me/${encodeURIComponent(username)}?start=login`;
      elements.authHelpText.textContent = "بعد از start کردن ربات، /login را بزن و کد ۶ رقمی را اینجا وارد کن.";
    } else {
      elements.openBotButton.href = "https://t.me/";
      elements.authHelpText.textContent = "Bot Username هنوز در پنل ادمین تنظیم نشده است. ربات را دستی باز کن و /login را بزن.";
    }
  } catch {
    elements.authHelpText.textContent = "اتصال به سرور برقرار نشد. تنظیمات دامنه، ربات و دیتابیس را بررسی کن.";
  }
}

async function browserLogin(event) {
  event.preventDefault();
  const code = elements.browserLoginCodeInput.value.trim();
  if (!code) return showToast("کد ورود را وارد کن.");

  setButtonLoading(elements.browserLoginButton, true, "در حال اتصال...");
  try {
    const result = await fetchJson("/api/auth/browser-login", {
      method: "POST",
      skipAuth: true,
      body: JSON.stringify({ code })
    });
    localStorage.setItem(storage.sessionToken, result.sessionToken);
    elements.browserLoginCodeInput.value = "";
    showToast("اکانت تلگرام وصل شد.");
    await loadAll();
  } catch (error) {
    showToast(error.message || "اتصال اکانت انجام نشد.");
  } finally {
    setButtonLoading(elements.browserLoginButton, false, "اتصال اکانت");
  }
}

function renderProfile(config) {
  const name = config.displayName || config.telegramUsername || config.userId;
  const plan = config.effectivePlan || { name: config.plan };
  elements.profileText.textContent = name;
  elements.planBadge.textContent = plan.name || config.plan || "Free";
  elements.planBadge.style.background = plan.badgeColor || "#16a34a";
  elements.planBadge.style.color = plan.badgeTextColor || "#ffffff";
  elements.accountStatusText.textContent = config.isActive ? `فعال · ${featureSummary(config.features)}` : "اکانت غیرفعال";
  const keyStatus = config.aiServerKeyConfigured
    ? `${toPersianNumber(config.aiServerKeysCount || 1)} کلید سرور فعال است`
    : "کلید را در اکانت وارد کن";
  elements.modelText.textContent = `${config.openRouterModel || "-"} · ${keyStatus}`;
}

function renderUserInfo(config) {
  elements.userIdText.textContent = config.userId;
  elements.userPlanText.textContent = config.isActive ? config.plan : `${config.plan} · غیرفعال`;
  elements.storageProviderText.textContent = config.storageProvider === "postgres" ? "PostgreSQL" : "Local file";
  elements.userFeaturesText.textContent = featureSummary(config.features);
  elements.languageLevelInput.value = config.languageLevel || "B1";
}

function renderQuota(config) {
  const usage = config.usage || {};
  elements.cardQuotaText.textContent = usageLine("کارت AI", usage.card);
  elements.dictionaryQuotaText.textContent = usageLine("دیکشنری", usage.dictionary);
  elements.correctionQuotaText.textContent = usageLine("اصلاح متن", usage.correction);
  renderUsageReport(config);
}

function usageLine(label, usage) {
  if (!usage) return `${label}: -`;
  const daily = usage.dailyLimit < 0 ? "نامحدود" : `${toPersianNumber(usage.today)}/${toPersianNumber(usage.dailyLimit)}`;
  const monthly = usage.monthlyLimit < 0 ? "نامحدود" : `${toPersianNumber(usage.thisMonth)}/${toPersianNumber(usage.monthlyLimit)}`;
  return `${label} · روزانه ${daily} · ماهانه ${monthly}`;
}

function renderUsageReport(config) {
  const rows = [
    { key: "card", label: "کارت AI", usage: config.usage?.card },
    { key: "dictionary", label: "دیکشنری", usage: config.usage?.dictionary },
    { key: "correction", label: "اصلاح متن", usage: config.usage?.correction }
  ];
  const totalToday = rows.reduce((sum, row) => sum + Number(row.usage?.today || 0), 0);
  const totalLimit = rows.reduce((sum, row) => row.usage?.dailyLimit < 0 ? sum : sum + Number(row.usage?.dailyLimit || 0), 0);
  const hasUnlimited = rows.some(row => row.usage?.dailyLimit < 0);
  const planName = config.effectivePlan?.name || config.plan || "Free";

  elements.dailyReportText.textContent = hasUnlimited
    ? `امروز ${toPersianNumber(totalToday)} درخواست AI استفاده کردی. پلن ${planName} برای بخشی از ابزارها نامحدود است.`
    : `امروز ${toPersianNumber(totalToday)} درخواست از سهمیه روزانه ${toPersianNumber(totalLimit)} درخواست استفاده شده است.`;

  elements.usageGrid.innerHTML = rows.map(row => usageCard(row.label, row.usage)).join("");
}

function usageCard(label, usage) {
  if (!usage) {
    return `<article class="usage-item"><strong>${label}</strong><span>-</span><i><b style="width:0%"></b></i></article>`;
  }

  const used = Number(usage.today || 0);
  const unlimited = usage.dailyLimit < 0;
  const limitText = unlimited ? "نامحدود" : toPersianNumber(usage.dailyLimit);
  const percent = unlimited ? 100 : Math.min(100, Math.round((used / Math.max(usage.dailyLimit, 1)) * 100));
  return `
    <article class="usage-item">
      <strong>${label}</strong>
      <span>${toPersianNumber(used)} / ${limitText}</span>
      <i><b style="width:${percent}%"></b></i>
    </article>
  `;
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
  elements.accuracy.textContent = `${toPersianNumber(summary.accuracy)}٪`;
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
  elements.frontCaption.textContent = isFeedback ? "تمرین اصلاح" : "روی کارت";
  elements.backCaption.textContent = isFeedback ? "دلیل و الگو" : "پشت کارت";
  elements.frontText.textContent = card.front;
  elements.exampleText.textContent = "";
  elements.backText.textContent = card.back;
  elements.qaText.textContent = [card.prompt, card.answer, card.notes].filter(Boolean).join(" · ");
  setBoxProgress(card.box);
}

function revealCurrentCard() {
  elements.answerPanel.hidden = false;
  elements.revealButton.hidden = true;
  tg?.HapticFeedback?.impactOccurred("light");
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

async function saveCard(event) {
  event.preventDefault();
  const payload = Object.fromEntries(new FormData(elements.form).entries());
  const isEdit = Boolean(state.editingCardId);
  const url = isEdit ? `/api/cards/${state.editingCardId}` : "/api/cards";
  const method = isEdit ? "PUT" : "POST";
  try {
    await fetchJson(url, { method, body: JSON.stringify(payload) });
    showToast(isEdit ? "کارت ویرایش شد." : "کارت اضافه شد.");
    resetCardForm();
    await loadAll();
    switchView(isEdit ? "deck" : "review");
  } catch (error) {
    showToast(error.message || "کارت ذخیره نشد.");
  }
}

async function completeWithAi() {
  if (!hasFeature("ai")) return planLocked("کارت‌سازی با AI");
  const type = getSelectedType();
  if (type === "Feedback" && !hasFeature("feedbackCards")) return planLocked("کارت فیدبک");
  const text = elements.aiWordInput.value.trim() || elements.frontInput.value.trim();
  if (!text) {
    showToast(type === "Feedback" ? "اول اشتباه یا فیدبک استاد را بنویس." : "اول متن کارت را وارد کن.");
    return;
  }

  setButtonLoading(elements.completeAiButton, true, "در حال تکمیل...");
  try {
    const card = await fetchJson("/api/ai/complete", {
      method: "POST",
      headers: openRouterHeaders(),
      body: JSON.stringify({ text, type })
    });
    fillCardForm(card);
    showToast(type === "Feedback" ? "کارت فیدبک آماده شد." : "فیلدهای کارت پر شد.");
  } catch (error) {
    showToast(error.message || "تکمیل با OpenRouter انجام نشد.");
  } finally {
    setButtonLoading(elements.completeAiButton, false, "پر کن");
  }
}

async function lookupDictionary() {
  if (!hasFeature("dictionary")) return planLocked("دیکشنری هوشمند");
  const text = elements.dictionaryInput.value.trim();
  if (!text) return showToast("کلمه یا عبارت را وارد کن.");

  setButtonLoading(elements.dictionaryButton, true, "...");
  try {
    state.dictionary = await fetchJson("/api/ai/dictionary", {
      method: "POST",
      headers: openRouterHeaders(),
      body: JSON.stringify({ text })
    });
    renderDictionaryResult(state.dictionary);
    await loadAll();
  } catch (error) {
    showToast(error.message || "دیکشنری آماده نشد.");
  } finally {
    setButtonLoading(elements.dictionaryButton, false, "جستجو");
  }
}

function renderDictionaryResult(result) {
  elements.dictionaryResult.hidden = false;
  const synonyms = arrayOf(result.synonyms).map(item => `<span>${escapeHtml(item)}</span>`).join("");
  const examples = arrayOf(result.examples).map(item => `<li class="mixed">${escapeHtml(item)}</li>`).join("");
  elements.dictionaryResult.innerHTML = `
    <div class="result-head">
      <div>
        <strong class="mixed">${escapeHtml(result.word || "-")}</strong>
        <small class="mixed">${escapeHtml([result.pronunciation, result.partOfSpeech].filter(Boolean).join(" · "))}</small>
      </div>
      <button class="secondary-button compact" data-action="add-dictionary-card" type="button">افزودن به لایتنر</button>
    </div>
    <div class="definition-grid">
      <section><small>معنی فارسی</small><p class="mixed">${escapeHtml(result.persianMeaning || "-")}</p></section>
      <section><small>تعریف ساده</small><p class="mixed">${escapeHtml(result.englishDefinition || "-")}</p></section>
    </div>
    <div class="tag-row">${synonyms}</div>
    <ul class="example-list">${examples}</ul>
    <p class="note-box mixed">${escapeHtml(result.notes || "")}</p>
  `;
  elements.dictionaryResult.querySelector('[data-action="add-dictionary-card"]').addEventListener("click", addDictionaryToDeck);
}

async function addDictionaryToDeck() {
  const result = state.dictionary;
  if (!result) return;
  try {
    await fetchJson("/api/cards", {
      method: "POST",
      body: JSON.stringify({
        type: "Word",
        front: result.word,
        back: [result.persianMeaning, result.englishDefinition].filter(Boolean).join("\n\n"),
        example: arrayOf(result.examples)[0] || "",
        prompt: `What does "${result.word}" mean?`,
        answer: result.persianMeaning || result.englishDefinition || "",
        notes: result.notes || ""
      })
    });
    showToast("از دیکشنری به لایتنر اضافه شد.");
    await loadAll();
  } catch (error) {
    showToast(error.message || "کارت ذخیره نشد.");
  }
}

async function correctText() {
  if (!hasFeature("textCorrection")) return planLocked("اصلاح متن");
  const text = elements.correctionInput.value.trim();
  if (!text) return showToast("جمله یا متن را وارد کن.");

  setButtonLoading(elements.correctionButton, true, "در حال تحلیل...");
  try {
    state.correction = await fetchJson("/api/ai/correction", {
      method: "POST",
      headers: openRouterHeaders(),
      body: JSON.stringify({ text })
    });
    renderCorrectionResult(state.correction);
    await loadAll();
  } catch (error) {
    showToast(error.message || "تحلیل متن انجام نشد.");
  } finally {
    setButtonLoading(elements.correctionButton, false, "تحلیل کن");
  }
}

function renderCorrectionResult(result) {
  elements.correctionResult.hidden = false;
  const issues = arrayOf(result.issues).map(issue => `
    <article class="issue-card ${escapeHtml(issue.severity || "low")}">
      <span>${severityLabel(issue.severity)}</span>
      <div class="issue-lines">
        <p class="mixed wrong">${escapeHtml(issue.original || "-")}</p>
        <p class="mixed right">${escapeHtml(issue.corrected || "-")}</p>
      </div>
      <small class="mixed">${escapeHtml(issue.reason || "")}</small>
    </article>
  `).join("");
  const alternatives = arrayOf(result.betterAlternatives).map(item => `<li class="mixed">${escapeHtml(item)}</li>`).join("");
  elements.correctionResult.innerHTML = `
    <div class="correction-compare">
      <section>
        <small>متن اولیه</small>
        <p class="mixed">${escapeHtml(result.original || "")}</p>
      </section>
      <section class="corrected">
        <small>نسخه درست</small>
        <p class="mixed">${escapeHtml(result.corrected || "")}</p>
      </section>
    </div>
    <div class="definition-grid">
      <section><small>ترجمه</small><p class="mixed">${escapeHtml(result.persianTranslation || "-")}</p></section>
      <section><small>جمع‌بندی</small><p class="mixed">${escapeHtml(result.overallNote || "-")}</p></section>
    </div>
    <div class="issues-grid">${issues || `<p class="note-box">اشتباه مشخصی پیدا نشد.</p>`}</div>
    <ul class="example-list">${alternatives}</ul>
    <button class="secondary-button" data-action="add-feedback-card" type="button">افزودن به فیدبک‌ها</button>
  `;
  elements.correctionResult.querySelector('[data-action="add-feedback-card"]').addEventListener("click", addCorrectionToFeedback);
}

async function addCorrectionToFeedback() {
  const result = state.correction;
  if (!result) return;
  try {
    await fetchJson("/api/cards", {
      method: "POST",
      body: JSON.stringify({
        type: "Feedback",
        front: `wrong: ${result.original} -> correct: ${result.corrected}`,
        back: result.overallNote || "",
        example: result.corrected || "",
        prompt: `Correct this sentence: ${result.original}`,
        answer: result.corrected || "",
        notes: arrayOf(result.issues).map(item => `${item.original} -> ${item.corrected}: ${item.reason}`).join("\n")
      })
    });
    showToast("به کارت‌های فیدبک اضافه شد.");
    await loadAll();
  } catch (error) {
    showToast(error.message || "کارت فیدبک ذخیره نشد.");
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
  elements.backInput.required = !isFeedback;
  elements.exampleInput.placeholder = placeholders[2];
  elements.promptInput.placeholder = placeholders[3];
  elements.answerInput.placeholder = placeholders[4];
  elements.notesInput.placeholder = placeholders[5];
}

function getSelectedType() {
  return document.querySelector('input[name="type"]:checked')?.value || "Word";
}

function resetCardForm() {
  state.editingCardId = null;
  elements.form.reset();
  elements.formTitle.textContent = "کارت جدید";
  elements.saveCardButton.textContent = "افزودن به لایتنر";
  elements.cancelEditButton.hidden = true;
  updateCardMode();
}

function loadSettings() {
  elements.apiKeyInput.value = localStorage.getItem(storage.apiKey) || "";
}

function saveSettings(event) {
  event.preventDefault();
  localStorage.setItem(storage.apiKey, elements.apiKeyInput.value.trim());
  showToast("کلید ذخیره شد.");
}

async function redeemCode(event) {
  event.preventDefault();
  const code = elements.redeemCodeInput.value.trim();
  if (!code) return showToast("کد فعال‌سازی را وارد کن.");
  setButtonLoading(elements.redeemCodeButton, true, "در حال فعال‌سازی...");
  try {
    await fetchJson("/api/access/redeem", {
      method: "POST",
      body: JSON.stringify({ code })
    });
    showToast("کد فعال شد.");
    elements.redeemCodeInput.value = "";
    await loadAll();
  } catch (error) {
    showToast(error.message || "کد فعال نشد.");
  } finally {
    setButtonLoading(elements.redeemCodeButton, false, "فعال‌سازی کد");
  }
}

async function savePreferences() {
  try {
    await fetchJson("/api/profile/preferences", {
      method: "PUT",
      body: JSON.stringify({ languageLevel: elements.languageLevelInput.value })
    });
    showToast("تنظیمات ذخیره شد.");
    await loadAll();
  } catch (error) {
    showToast(error.message || "تنظیمات ذخیره نشد.");
  }
}

async function exportDeck() {
  if (!hasFeature("exportImport")) return planLocked("خروجی و ورودی دیتا");
  try {
    const data = await fetchJson("/api/export");
    const blob = new Blob(["\ufeff", JSON.stringify(data, null, 2)], { type: "application/json;charset=utf-8" });
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
  if (!hasFeature("exportImport")) {
    event.target.value = "";
    return planLocked("خروجی و ورودی دیتا");
  }
  const file = event.target.files?.[0];
  if (!file) return;
  try {
    const text = (await file.text()).replace(/^\uFEFF/, "");
    const payload = JSON.parse(text);
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
    <article class="deck-item ${card.type === "Feedback" ? "feedback-item" : ""}" data-card-id="${escapeHtml(card.id)}">
      <div class="deck-head">
        <h3 class="mixed">${escapeHtml(card.front)}</h3>
        <span>${typeLabels[card.type] ?? "کارت"}</span>
      </div>
      <div class="deck-back mixed" hidden>${escapeHtml(card.back)}</div>
      <footer>
        <span>جعبه ${toPersianNumber(card.box)}</span>
        <span class="review-date">${escapeHtml(nextReviewLabel(card.nextReviewAt))}</span>
        <div class="deck-actions">
          <button class="mini-button" type="button" data-action="toggle-back">نمایش پشت</button>
          <button class="mini-button" type="button" data-action="edit">ویرایش</button>
          <button class="mini-button danger" type="button" data-action="delete">حذف</button>
        </div>
      </footer>
    </article>
  `).join("") : `<div class="empty-state"><strong>هنوز کارتی نداری.</strong><span>از بخش افزودن شروع کن.</span></div>`;

  elements.deckList.querySelectorAll(".deck-item").forEach((item) => {
    const id = item.dataset.cardId;
    item.querySelector('[data-action="toggle-back"]').addEventListener("click", (event) => toggleDeckBack(item, event.currentTarget));
    item.querySelector('[data-action="edit"]').addEventListener("click", () => startEditCard(id));
    item.querySelector('[data-action="delete"]').addEventListener("click", () => deleteCard(id));
  });
}

function toggleDeckBack(item, button) {
  const back = item.querySelector(".deck-back");
  back.hidden = !back.hidden;
  button.textContent = back.hidden ? "نمایش پشت" : "پنهان کردن";
}

function startEditCard(id) {
  const card = state.cards.find(item => item.id === id);
  if (!card) return;
  state.editingCardId = id;
  fillCardForm(card);
  elements.formTitle.textContent = "ویرایش کارت";
  elements.saveCardButton.textContent = "ذخیره تغییرات";
  elements.cancelEditButton.hidden = false;
  switchView("add");
}

async function deleteCard(id) {
  const ok = confirm("این کارت حذف شود؟");
  if (!ok) return;
  try {
    await fetchJson(`/api/cards/${id}`, { method: "DELETE" });
    showToast("کارت حذف شد.");
    await loadAll();
  } catch (error) {
    showToast(error.message || "حذف کارت انجام نشد.");
  }
}

async function fetchJson(url, options = {}) {
  const headers = new Headers(options.headers || {});
  headers.set("Content-Type", "application/json");
  if (!options.skipAuth) {
    headers.set("X-Dev-User-Id", devUserId);
    const sessionToken = localStorage.getItem(storage.sessionToken) || "";
    if (sessionToken) headers.set("X-Session-Token", sessionToken);
  }
  if (tg?.initData) headers.set("X-Telegram-Init-Data", tg.initData);

  const response = await fetch(url, { ...options, headers });
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    const failure = new Error(error.message || "درخواست ناموفق بود.");
    failure.status = response.status;
    throw failure;
  }
  return response.status === 204 ? null : response.json();
}

function registerPwa() {
  if (!("serviceWorker" in navigator)) return;
  window.addEventListener("load", () => {
    navigator.serviceWorker.register("/service-worker.js").then((registration) => {
      registration.update().catch(() => {});
    }).catch(() => {});
  });
}

async function checkForAppUpdate() {
  if (!("serviceWorker" in navigator)) return showToast("PWA روی این مرورگر فعال نیست.");
  setButtonLoading(elements.checkUpdateButton, true, "در حال بررسی...");
  try {
    const registration = await navigator.serviceWorker.getRegistration();
    if (registration) {
      await registration.update();
    }
    showToast("آخرین نسخه بررسی شد. صفحه دوباره بارگذاری می‌شود.");
    setTimeout(() => window.location.reload(), 700);
  } catch {
    showToast("بررسی آپدیت انجام نشد.");
  } finally {
    setButtonLoading(elements.checkUpdateButton, false, "بررسی آپدیت");
  }
}

function showLoadError(error) {
  elements.reviewCard.hidden = true;
  elements.emptyState.hidden = false;
  const title = elements.emptyState.querySelector("strong");
  const text = elements.emptyState.querySelector("span");
  if (title) title.textContent = "دیتا لود نشد.";
  if (text) text.textContent = error.message || "اتصال سرور یا دیتابیس را بررسی کن.";
  elements.accountStatusText.textContent = "خطا در بارگذاری";
  elements.dueCards.textContent = "۰";
  elements.totalCards.textContent = "۰";
  elements.accuracy.textContent = "۰٪";
}

function featureSummary(features = {}) {
  const enabled = [];
  if (features.ai) enabled.push("کارت AI");
  if (features.dictionary) enabled.push("دیکشنری");
  if (features.textCorrection) enabled.push("اصلاح متن");
  if (features.feedbackCards) enabled.push("فیدبک");
  if (features.exportImport) enabled.push("خروجی/ورودی");
  if (features.unlimitedCards) enabled.push("کارت نامحدود");
  return enabled.length ? enabled.join(" · ") : "محدود";
}

function hasFeature(key) {
  return Boolean(state.config?.features?.[key]);
}

function planLocked(name) {
  showToast(`${name} در پلن فعلی فعال نیست.`);
}

function nextReviewLabel(value) {
  if (!value) return "مرور: -";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "مرور: -";
  const due = date <= new Date();
  const formatted = new Intl.DateTimeFormat("fa-IR-u-ca-persian", {
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit"
  }).format(date);
  return due ? `آماده مرور · ${formatted}` : `مرور بعدی · ${formatted}`;
}

function openRouterHeaders() {
  const apiKey = localStorage.getItem(storage.apiKey) || "";
  return apiKey ? { "X-OpenRouter-Api-Key": apiKey } : {};
}

function setButtonLoading(button, loading, text) {
  button.disabled = loading;
  button.textContent = text;
}

function severityLabel(value) {
  if (value === "high") return "مهم";
  if (value === "medium") return "متوسط";
  return "جزئی";
}

function arrayOf(value) {
  return Array.isArray(value) ? value.filter(Boolean) : [];
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
  showToast.timer = setTimeout(() => elements.toast.classList.remove("show"), 2800);
}

function toPersianNumber(value) {
  return String(value).replace(/\d/g, (digit) => "۰۱۲۳۴۵۶۷۸۹"[digit]);
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}
