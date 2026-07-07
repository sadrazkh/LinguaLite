const storage = { token: "lingualite.adminToken" };

const $ = (selector) => document.querySelector(selector);
const elements = {
  loginForm: $("#adminLoginForm"),
  tokenInput: $("#adminTokenInput"),
  logoutButton: $("#adminLogoutButton"),
  status: $("#adminStatus"),
  dashboard: $("#adminDashboard"),
  usersCount: $("#usersCount"),
  plansCount: $("#plansCount"),
  codesCount: $("#codesCount"),
  settingsForm: $("#settingsForm"),
  openRouterModelInput: $("#openRouterModelInput"),
  openRouterRefererInput: $("#openRouterRefererInput"),
  publicBaseUrlInput: $("#publicBaseUrlInput"),
  miniAppUrlInput: $("#miniAppUrlInput"),
  botUsernameInput: $("#botUsernameInput"),
  reminderHourInput: $("#reminderHourInput"),
  botEnabledInput: $("#botEnabledInput"),
  remindersEnabledInput: $("#remindersEnabledInput"),
  setWebhookButton: $("#setWebhookButton"),
  botStatusButton: $("#botStatusButton"),
  botStatusPanel: $("#botStatusPanel"),
  newPlanNameInput: $("#newPlanNameInput"),
  addPlanButton: $("#addPlanButton"),
  plansBoard: $("#plansBoard"),
  usersList: $("#usersList"),
  usersPagination: $("#usersPagination"),
  prevUsersPageButton: $("#prevUsersPageButton"),
  nextUsersPageButton: $("#nextUsersPageButton"),
  usersPageText: $("#usersPageText"),
  broadcastAudienceInput: $("#broadcastAudienceInput"),
  broadcastPlanInput: $("#broadcastPlanInput"),
  broadcastActiveInput: $("#broadcastActiveInput"),
  broadcastSourceInput: $("#broadcastSourceInput"),
  broadcastAccessCodeInput: $("#broadcastAccessCodeInput"),
  broadcastSearchInput: $("#broadcastSearchInput"),
  broadcastMessageInput: $("#broadcastMessageInput"),
  broadcastPreview: $("#broadcastPreview"),
  previewBroadcastButton: $("#previewBroadcastButton"),
  sendBroadcastButton: $("#sendBroadcastButton"),
  customCodeInput: $("#customCodeInput"),
  codePlanInput: $("#codePlanInput"),
  codeMaxUsesInput: $("#codeMaxUsesInput"),
  createCodeButton: $("#createCodeButton"),
  codesList: $("#codesList"),
  toast: $("#toast")
};

let state = { users: [], metrics: [], plans: [], codes: [], codeUsage: [], settings: null, draggingPlanId: null, usersPage: 1, usersPageSize: 10 };

elements.loginForm.addEventListener("submit", adminLogin);
elements.logoutButton.addEventListener("click", logout);
elements.settingsForm.addEventListener("submit", saveSettings);
elements.setWebhookButton.addEventListener("click", setWebhook);
elements.botStatusButton.addEventListener("click", loadBotStatus);
elements.addPlanButton.addEventListener("click", addPlan);
elements.createCodeButton.addEventListener("click", createCode);
elements.prevUsersPageButton.addEventListener("click", () => changeUsersPage(-1));
elements.nextUsersPageButton.addEventListener("click", () => changeUsersPage(1));
elements.previewBroadcastButton.addEventListener("click", renderBroadcastPreview);
elements.sendBroadcastButton.addEventListener("click", sendBroadcast);
[elements.broadcastAudienceInput, elements.broadcastPlanInput, elements.broadcastActiveInput, elements.broadcastSourceInput, elements.broadcastAccessCodeInput, elements.broadcastSearchInput]
  .forEach(input => input.addEventListener("input", renderBroadcastPreview));

const savedToken = localStorage.getItem(storage.token);
if (savedToken) {
  elements.tokenInput.value = savedToken;
  loadAll().catch(() => logout("توکن ذخیره‌شده معتبر نیست."));
}

async function adminLogin(event) {
  event.preventDefault();
  const token = elements.tokenInput.value.trim();
  if (!token) return setStatus("توکن را وارد کن.", "error");
  localStorage.setItem(storage.token, token);
  await loadAll();
}

async function loadAll() {
  setStatus("در حال بارگذاری...", "");
  const [users, metrics, codes, codeUsage, plans, settingsPayload] = await Promise.all([
    adminFetch("/api/admin/users"),
    adminFetch("/api/admin/user-metrics"),
    adminFetch("/api/admin/codes"),
    adminFetch("/api/admin/codes/usage"),
    adminFetch("/api/admin/plans"),
    adminFetch("/api/admin/settings")
  ]);
  state = { ...state, users, metrics, codes, codeUsage, plans, settings: settingsPayload.settings };
  state.usersPage = Math.min(state.usersPage, getUsersPageCount());
  elements.dashboard.hidden = false;
  elements.logoutButton.hidden = false;
  setStatus("ادمین تایید شد.", "ok");
  renderStats();
  renderSettings(settingsPayload);
  renderPlans();
  renderUsers();
  renderCodes();
  renderBroadcastPreview();
}

function renderStats() {
  elements.usersCount.textContent = toPersianNumber(state.users.length);
  elements.plansCount.textContent = toPersianNumber(state.plans.length);
  elements.codesCount.textContent = toPersianNumber(state.codes.length);
}

function renderSettings(payload) {
  const settings = payload.settings || {};
  const effective = payload.effectiveOpenRouter || {};
  const telegram = payload.effectiveTelegram || {};
  elements.openRouterModelInput.value = settings.openRouterModel || effective.defaultModel || "";
  elements.openRouterRefererInput.value = settings.openRouterReferer || effective.referer || "";
  elements.publicBaseUrlInput.value = settings.publicBaseUrl || telegram.publicBaseUrl || "";
  elements.miniAppUrlInput.value = settings.telegramMiniAppUrl || telegram.telegramMiniAppUrl || "";
  elements.botUsernameInput.value = settings.telegramBotUsername || telegram.telegramBotUsername || "";
  elements.reminderHourInput.value = settings.reminderHour ?? 9;
  elements.botEnabledInput.checked = settings.botEnabled ?? true;
  elements.remindersEnabledInput.checked = settings.remindersEnabled ?? true;
  renderBotStatusSummary(telegram);
}

function renderPlans() {
  elements.codePlanInput.innerHTML = state.plans
    .map(plan => `<option value="${escapeHtml(plan.name)}">${escapeHtml(plan.name)}</option>`)
    .join("");
  elements.broadcastPlanInput.innerHTML = `<option value="">همه پلن‌ها</option>` + state.plans
    .map(plan => `<option value="${escapeHtml(plan.name)}">${escapeHtml(plan.name)}</option>`)
    .join("");

  elements.plansBoard.innerHTML = state.plans.map(plan => `
    <article class="plan-card" draggable="true" data-plan-id="${escapeHtml(plan.id)}">
      <div class="plan-head">
        <span class="plan-preview" style="background:${safeColor(plan.badgeColor, "#16a34a")};color:${safeColor(plan.badgeTextColor, "#ffffff")}">${escapeHtml(plan.name)}</span>
        <span class="plan-grip">↕</span>
      </div>
      <div class="plan-fields">
        <label>نام<input data-field="name" value="${escapeHtml(plan.name)}"></label>
        <label>شناسه<input data-field="id" value="${escapeHtml(plan.id)}" ${plan.isDefault ? "disabled" : ""}></label>
        <label>رنگ بج<input data-field="badgeColor" type="color" value="${safeColor(plan.badgeColor, "#16a34a")}"></label>
        <label>رنگ متن<input data-field="badgeTextColor" type="color" value="${safeColor(plan.badgeTextColor, "#ffffff")}"></label>
        <label>AI کارت روزانه<input data-field="aiDailyLimit" type="number" value="${plan.aiDailyLimit}"></label>
        <label>AI کارت ماهانه<input data-field="aiMonthlyLimit" type="number" value="${plan.aiMonthlyLimit}"></label>
        <label>دیکشنری روزانه<input data-field="dictionaryDailyLimit" type="number" value="${plan.dictionaryDailyLimit}"></label>
        <label>دیکشنری ماهانه<input data-field="dictionaryMonthlyLimit" type="number" value="${plan.dictionaryMonthlyLimit}"></label>
        <label>اصلاح روزانه<input data-field="correctionDailyLimit" type="number" value="${plan.correctionDailyLimit}"></label>
        <label>اصلاح ماهانه<input data-field="correctionMonthlyLimit" type="number" value="${plan.correctionMonthlyLimit}"></label>
        <label>سقف کارت<input data-field="cardLimit" type="number" value="${plan.cardLimit}"></label>
        <label>ترتیب<input data-field="sortOrder" type="number" value="${plan.sortOrder}"></label>
      </div>
      <div class="feature-line">
        ${featureCheckbox("ai", "AI کارت", plan.features.ai)}
        ${featureCheckbox("dictionary", "دیکشنری", plan.features.dictionary)}
        ${featureCheckbox("textCorrection", "اصلاح متن", plan.features.textCorrection)}
        ${featureCheckbox("feedbackCards", "فیدبک", plan.features.feedbackCards)}
        ${featureCheckbox("exportImport", "Import", plan.features.exportImport)}
        ${featureCheckbox("unlimitedCards", "نامحدود", plan.features.unlimitedCards)}
      </div>
      <label class="check-row"><input data-field="isDefault" type="checkbox" ${plan.isDefault ? "checked" : ""}> <span>پیش‌فرض</span></label>
      <div class="tool-row">
        <button class="secondary-button" type="button" data-action="save">ذخیره</button>
        <button class="secondary-button danger" type="button" data-action="delete" ${plan.isDefault ? "disabled" : ""}>حذف</button>
      </div>
    </article>
  `).join("");

  elements.plansBoard.querySelectorAll(".plan-card").forEach(card => {
    card.addEventListener("dragstart", () => {
      state.draggingPlanId = card.dataset.planId;
      card.classList.add("dragging");
    });
    card.addEventListener("dragend", () => card.classList.remove("dragging"));
    card.addEventListener("dragover", event => event.preventDefault());
    card.addEventListener("drop", () => reorderPlans(state.draggingPlanId, card.dataset.planId));
    card.querySelector('[data-action="save"]').addEventListener("click", () => savePlan(card));
    card.querySelector('[data-action="delete"]').addEventListener("click", () => deletePlan(card.dataset.planId));
  });
}

function renderUsers() {
  const pageCount = getUsersPageCount();
  state.usersPage = Math.min(Math.max(state.usersPage, 1), pageCount);
  const start = (state.usersPage - 1) * state.usersPageSize;
  const pageUsers = state.users.slice(start, start + state.usersPageSize);

  elements.usersList.innerHTML = pageUsers.map(user => {
    const plan = findPlan(user.plan);
    const metrics = findMetrics(user.id);
    return `
      <article class="admin-item">
        <div>
          <strong>${escapeHtml(user.displayName || user.id)}</strong>
          <small>${escapeHtml(user.id)} · tg:${escapeHtml(user.telegramId || "-")} · @${escapeHtml(user.telegramUsername || "-")}</small>
          <small>${escapeHtml(user.source)} · <b class="inline-badge" style="background:${safeColor(plan?.badgeColor, "#16a34a")};color:${safeColor(plan?.badgeTextColor, "#ffffff")}">${escapeHtml(user.plan)}</b> · سطح ${escapeHtml(user.languageLevel || "B1")} · ${user.isActive ? "فعال" : "غیرفعال"} · کد: ${escapeHtml(user.accessCode || "-")} · ${escapeHtml(featureSummary(user.features))}</small>
          <div class="user-metrics">
            <span>کارت: ${toPersianNumber(metrics.totalCards)}</span>
            <span>مرور آماده: ${toPersianNumber(metrics.dueCards)}</span>
            <span>فعالیت امروز: ${toPersianNumber(metrics.activeMinutesToday)} دقیقه</span>
            <span>درخواست امروز: ${toPersianNumber(metrics.requestsToday)}</span>
            <span>افزوده امروز: ${toPersianNumber(metrics.cardsAddedToday)}</span>
            <span>مرور امروز: ${toPersianNumber(metrics.reviewsToday)}</span>
            <span>AI کارت: ${toPersianNumber(metrics.aiCardToday)}</span>
            <span>دیکشنری: ${toPersianNumber(metrics.aiDictionaryToday)}</span>
            <span>اصلاح: ${toPersianNumber(metrics.aiCorrectionToday)}</span>
          </div>
        </div>
        <div class="user-controls">
          <select data-user-field="plan">${state.plans.map(planItem => `<option value="${escapeHtml(planItem.name)}" ${planItem.name === user.plan ? "selected" : ""}>${escapeHtml(planItem.name)}</option>`).join("")}</select>
          <button class="mini-button" type="button" data-action="toggle-active">${user.isActive ? "غیرفعال" : "فعال"}</button>
          <button class="mini-button" type="button" data-action="toggle-reminder">${user.remindersEnabled ? "قطع یادآوری" : "فعال یادآوری"}</button>
        </div>
      </article>
    `;
  }).join("");

  elements.usersList.querySelectorAll(".admin-item").forEach((item, index) => {
    const user = pageUsers[index];
    item.querySelector('[data-user-field="plan"]').addEventListener("change", event => updateUser(user.id, { plan: event.target.value }));
    item.querySelector('[data-action="toggle-active"]').addEventListener("click", () => updateUser(user.id, { isActive: !user.isActive }));
    item.querySelector('[data-action="toggle-reminder"]').addEventListener("click", () => updateUser(user.id, { remindersEnabled: !user.remindersEnabled }));
  });

  elements.usersPagination.hidden = state.users.length <= state.usersPageSize;
  elements.usersPageText.textContent = `صفحه ${toPersianNumber(state.usersPage)} از ${toPersianNumber(pageCount)} · ${toPersianNumber(state.users.length)} کاربر`;
  elements.prevUsersPageButton.disabled = state.usersPage <= 1;
  elements.nextUsersPageButton.disabled = state.usersPage >= pageCount;
}

function renderCodes() {
  elements.codesList.innerHTML = state.codes.map(code => {
    const plan = findPlan(code.plan);
    const usage = findCodeUsage(code.code);
    const users = usage.users.slice(0, 6).map(user => `
      <span class="code-user-chip">
        ${escapeHtml(user.displayName || user.userId)}
        <small>@${escapeHtml(user.telegramUsername || "-")} · ${formatAdminDate(user.lastSeenAt)}</small>
      </span>
    `).join("");
    return `
      <article class="admin-item code-item" data-code="${escapeHtml(code.code)}">
        <div>
          <strong>${escapeHtml(code.code)}</strong>
          <small><b class="inline-badge" style="background:${safeColor(plan?.badgeColor, "#16a34a")};color:${safeColor(plan?.badgeTextColor, "#ffffff")}">${escapeHtml(code.plan)}</b> · ${toPersianNumber(code.uses)}/${toPersianNumber(code.maxUses)}</small>
          <div class="code-users">
            <b>استفاده‌کننده‌ها: ${toPersianNumber(usage.usersCount)}</b>
            ${users || "<span>هنوز کسی با این کد فعال نکرده.</span>"}
          </div>
        </div>
        <div class="code-controls">
          <select data-code-field="plan">${state.plans.map(planItem => `<option value="${escapeHtml(planItem.name)}" ${planItem.name === code.plan ? "selected" : ""}>${escapeHtml(planItem.name)}</option>`).join("")}</select>
          <input data-code-field="maxUses" type="number" min="${code.uses}" value="${code.maxUses}">
          <button class="mini-button" type="button" data-action="save-code">ذخیره</button>
        </div>
      </article>
    `;
  }).join("");

  elements.codesList.querySelectorAll(".code-item").forEach(item => {
    item.querySelector('[data-action="save-code"]').addEventListener("click", () => saveAccessCode(item));
  });
}

async function saveSettings(event) {
  event.preventDefault();
  await adminFetch("/api/admin/settings", {
    method: "PUT",
    body: JSON.stringify({
      openRouterModel: elements.openRouterModelInput.value.trim(),
      openRouterReferer: elements.openRouterRefererInput.value.trim(),
      publicBaseUrl: elements.publicBaseUrlInput.value.trim(),
      telegramMiniAppUrl: elements.miniAppUrlInput.value.trim(),
      telegramBotUsername: elements.botUsernameInput.value.trim(),
      reminderHour: Number(elements.reminderHourInput.value || 9),
      botEnabled: elements.botEnabledInput.checked,
      remindersEnabled: elements.remindersEnabledInput.checked
    })
  });
  showToast("تنظیمات ذخیره شد.");
  await loadAll();
}

async function setWebhook() {
  setButtonLoading(elements.setWebhookButton, true, "در حال تنظیم...");
  try {
    const result = await adminFetch("/api/admin/bot/set-webhook", { method: "POST" });
    showToast(result.ok ? "Webhook تنظیم شد." : result.message || "Webhook تنظیم نشد.");
    await loadBotStatus();
  } catch (error) {
    showToast(error.message || "Webhook تنظیم نشد.");
  } finally {
    setButtonLoading(elements.setWebhookButton, false, "تنظیم Webhook ربات");
  }
}

async function loadBotStatus() {
  setButtonLoading(elements.botStatusButton, true, "در حال بررسی...");
  try {
    const result = await adminFetch("/api/admin/bot/status");
    renderBotStatus(result);
  } catch (error) {
    elements.botStatusPanel.hidden = false;
    elements.botStatusPanel.innerHTML = `<strong class="danger-text">وضعیت ربات خوانده نشد.</strong><span>${escapeHtml(error.message)}</span>`;
  } finally {
    setButtonLoading(elements.botStatusButton, false, "بررسی وضعیت ربات");
  }
}

function renderBotStatusSummary(telegram) {
  elements.botStatusPanel.hidden = false;
  const tokenText = telegram.botTokenConfigured ? "توکن روی سرور تنظیم شده" : "توکن روی سرور تنظیم نشده";
  elements.botStatusPanel.innerHTML = `
    <div class="bot-status-grid">
      <span><b>Webhook مورد انتظار</b><code>${escapeHtml(telegram.webhookUrl || "-")}</code></span>
      <span><b>Mini App URL</b><code>${escapeHtml(telegram.telegramMiniAppUrl || "-")}</code></span>
      <span><b>Bot Username</b><code>${escapeHtml(telegram.telegramBotUsername || "-")}</code></span>
      <span class="${telegram.botTokenConfigured ? "ok-text" : "danger-text"}"><b>Token</b>${tokenText}</span>
    </div>
  `;
}

function renderBotStatus(result) {
  elements.botStatusPanel.hidden = false;
  if (!result.tokenConfigured) {
    elements.botStatusPanel.innerHTML = `<strong class="danger-text">TELEGRAM_BOT_TOKEN تنظیم نشده است.</strong>`;
    return;
  }

  const me = parseTelegramBody(result.me);
  const webhook = parseTelegramBody(result.webhook);
  const bot = me?.result || {};
  const info = webhook?.result || {};
  const expected = result.expectedWebhookUrl || "";
  const actual = info.url || "";
  const matches = expected && actual && normalizeUrl(expected) === normalizeUrl(actual);
  const hasError = Boolean(info.last_error_message);

  elements.botStatusPanel.innerHTML = `
    <div class="bot-status-head">
      <strong class="${result.ok && matches && !hasError ? "ok-text" : "warning-text"}">${result.ok ? "ربات قابل دسترسی است" : "ربات خطا دارد"}</strong>
      <small>@${escapeHtml(bot.username || "-")} · id:${escapeHtml(bot.id || "-")}</small>
    </div>
    <div class="bot-status-grid">
      <span><b>Webhook ثبت‌شده</b><code>${escapeHtml(actual || "-")}</code></span>
      <span><b>Webhook مورد انتظار</b><code>${escapeHtml(expected || "-")}</code></span>
      <span class="${matches ? "ok-text" : "danger-text"}"><b>تطابق URL</b>${matches ? "درست است" : "نیاز به تنظیم Webhook"}</span>
      <span><b>آپدیت‌های مانده</b>${toPersianNumber(info.pending_update_count ?? 0)}</span>
      <span class="${hasError ? "danger-text" : "ok-text"}"><b>خطای آخر</b>${escapeHtml(info.last_error_message || "ندارد")}</span>
      <span><b>وضعیت getMe</b>${escapeHtml(result.me?.description || "ok")}</span>
    </div>
  `;
}

function renderBroadcastPreview() {
  const users = filterBroadcastUsers();
  const reachable = users.filter(user => Boolean(user.telegramChatId));
  elements.broadcastPreview.innerHTML = `
    <div class="broadcast-summary">
      <strong>${toPersianNumber(users.length)} کاربر مطابق فیلتر</strong>
      <span>${toPersianNumber(reachable.length)} نفر قابل ارسال با ربات</span>
    </div>
    <div class="broadcast-users">
      ${users.slice(0, 24).map(user => `
        <label class="${user.telegramChatId ? "" : "muted-user"}">
          <input type="checkbox" data-broadcast-user="${escapeHtml(user.id)}" ${user.telegramChatId ? "checked" : "disabled"}>
          <span>${escapeHtml(user.displayName || user.id)} <small>@${escapeHtml(user.telegramUsername || "-")} · ${escapeHtml(user.plan)} · ${user.telegramChatId ? "ربات وصل" : "بدون chat id"}</small></span>
        </label>
      `).join("") || "<p>کاربری با این فیلتر پیدا نشد.</p>"}
    </div>
  `;
}

function filterBroadcastUsers() {
  const plan = elements.broadcastPlanInput.value;
  const active = elements.broadcastActiveInput.value;
  const source = elements.broadcastSourceInput.value;
  const accessCode = elements.broadcastAccessCodeInput.value.trim();
  const search = elements.broadcastSearchInput.value.trim().toLowerCase();

  return state.users.filter(user => {
    if (plan && user.plan !== plan) return false;
    if (active && String(user.isActive) !== active) return false;
    if (source && user.source !== source) return false;
    if (accessCode && String(user.accessCode || "").toLowerCase() !== accessCode.toLowerCase()) return false;
    if (search) {
      const haystack = [user.id, user.displayName, user.telegramId, user.telegramUsername].join(" ").toLowerCase();
      if (!haystack.includes(search)) return false;
    }
    return true;
  });
}

function readBroadcastPayload() {
  const audience = elements.broadcastAudienceInput.value;
  const selectedIds = [...elements.broadcastPreview.querySelectorAll("[data-broadcast-user]:checked")].map(input => input.dataset.broadcastUser);
  return {
    audience,
    userIds: audience === "selected" ? selectedIds : null,
    plan: elements.broadcastPlanInput.value || null,
    isActive: elements.broadcastActiveInput.value === "" ? null : elements.broadcastActiveInput.value === "true",
    source: elements.broadcastSourceInput.value || null,
    accessCode: elements.broadcastAccessCodeInput.value.trim() || null,
    search: elements.broadcastSearchInput.value.trim() || null,
    message: elements.broadcastMessageInput.value.trim()
  };
}

async function sendBroadcast() {
  const payload = readBroadcastPayload();
  if (!payload.message) return showToast("متن پیام را وارد کن.");
  if (payload.audience === "selected" && payload.userIds.length === 0) return showToast("حداقل یک کاربر را انتخاب کن.");

  setButtonLoading(elements.sendBroadcastButton, true, "در حال ارسال...");
  try {
    const result = await adminFetch("/api/admin/broadcast", {
      method: "POST",
      body: JSON.stringify(payload)
    });
    showToast(`ارسال شد: ${toPersianNumber(result.sent)} · رد شد: ${toPersianNumber(result.skipped)} · خطا: ${toPersianNumber(result.failed)}`);
  } catch (error) {
    showToast(error.message || "ارسال پیام انجام نشد.");
  } finally {
    setButtonLoading(elements.sendBroadcastButton, false, "ارسال پیام");
  }
}

async function addPlan() {
  const name = elements.newPlanNameInput.value.trim();
  if (!name) return showToast("نام پلن را وارد کن.");
  const id = slugify(name);
  await adminFetch(`/api/admin/plans/${encodeURIComponent(id)}`, {
    method: "PUT",
    body: JSON.stringify({
      id,
      name,
      badgeColor: "#2563eb",
      badgeTextColor: "#ffffff",
      features: defaultFeatures(),
      aiDailyLimit: 20,
      aiMonthlyLimit: 300,
      dictionaryDailyLimit: 30,
      dictionaryMonthlyLimit: 600,
      correctionDailyLimit: 15,
      correctionMonthlyLimit: 300,
      cardLimit: 200,
      sortOrder: state.plans.length,
      isDefault: false
    })
  });
  elements.newPlanNameInput.value = "";
  await loadAll();
}

async function savePlan(card) {
  const originalId = card.dataset.planId;
  const payload = readPlanCard(card);
  await adminFetch(`/api/admin/plans/${encodeURIComponent(originalId)}`, {
    method: "PUT",
    body: JSON.stringify(payload)
  });
  showToast("پلن ذخیره شد.");
  await loadAll();
}

async function deletePlan(id) {
  await adminFetch(`/api/admin/plans/${encodeURIComponent(id)}`, { method: "DELETE" });
  showToast("پلن حذف شد.");
  await loadAll();
}

async function reorderPlans(sourceId, targetId) {
  if (!sourceId || !targetId || sourceId === targetId) return;
  const plans = [...state.plans];
  const sourceIndex = plans.findIndex(plan => plan.id === sourceId);
  const targetIndex = plans.findIndex(plan => plan.id === targetId);
  if (sourceIndex < 0 || targetIndex < 0) return;
  const [moved] = plans.splice(sourceIndex, 1);
  plans.splice(targetIndex, 0, moved);
  await Promise.all(plans.map((plan, index) => adminFetch(`/api/admin/plans/${encodeURIComponent(plan.id)}`, {
    method: "PUT",
    body: JSON.stringify({ ...plan, sortOrder: index })
  })));
  await loadAll();
}

async function updateUser(id, payload) {
  await adminFetch(`/api/admin/users/${encodeURIComponent(id)}`, {
    method: "PUT",
    body: JSON.stringify(payload)
  });
  await loadAll();
}

async function createCode() {
  const planName = elements.codePlanInput.value;
  const plan = findPlan(planName) || state.plans[0];
  const code = await adminFetch("/api/admin/codes", {
    method: "POST",
    body: JSON.stringify({
      code: elements.customCodeInput.value.trim(),
      plan: plan?.name || "Free",
      maxUses: Number(elements.codeMaxUsesInput.value || 1),
      features: plan?.features || defaultFeatures()
    })
  });
  elements.customCodeInput.value = "";
  showToast(`کد ساخته شد: ${code.code}`);
  await loadAll();
}

async function saveAccessCode(item) {
  const code = item.dataset.code;
  const plan = findPlan(item.querySelector('[data-code-field="plan"]').value) || state.plans[0];
  const maxUses = Number(item.querySelector('[data-code-field="maxUses"]').value || 1);
  await adminFetch(`/api/admin/codes/${encodeURIComponent(code)}`, {
    method: "PUT",
    body: JSON.stringify({
      plan: plan?.name || "Free",
      features: plan?.features || defaultFeatures(),
      maxUses
    })
  });
  showToast("کد دسترسی ذخیره شد.");
  await loadAll();
}

function readPlanCard(card) {
  const get = field => card.querySelector(`[data-field="${field}"]`);
  return {
    id: get("id").value.trim(),
    name: get("name").value.trim(),
    badgeColor: get("badgeColor").value,
    badgeTextColor: get("badgeTextColor").value,
    aiDailyLimit: Number(get("aiDailyLimit").value),
    aiMonthlyLimit: Number(get("aiMonthlyLimit").value),
    dictionaryDailyLimit: Number(get("dictionaryDailyLimit").value),
    dictionaryMonthlyLimit: Number(get("dictionaryMonthlyLimit").value),
    correctionDailyLimit: Number(get("correctionDailyLimit").value),
    correctionMonthlyLimit: Number(get("correctionMonthlyLimit").value),
    cardLimit: Number(get("cardLimit").value),
    sortOrder: Number(get("sortOrder").value),
    isDefault: get("isDefault").checked,
    features: {
      ai: get("feature-ai").checked,
      dictionary: get("feature-dictionary").checked,
      textCorrection: get("feature-textCorrection").checked,
      feedbackCards: get("feature-feedbackCards").checked,
      exportImport: get("feature-exportImport").checked,
      unlimitedCards: get("feature-unlimitedCards").checked
    }
  };
}

function featureCheckbox(key, label, checked) {
  return `<label><input data-field="feature-${key}" type="checkbox" ${checked ? "checked" : ""}> ${label}</label>`;
}

async function adminFetch(url, options = {}) {
  const headers = new Headers(options.headers || {});
  headers.set("Content-Type", "application/json");
  headers.set("X-Admin-Token", localStorage.getItem(storage.token) || "");
  const response = await fetch(url, { ...options, headers });
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    throw new Error(error.message || "دسترسی ادمین تایید نشد.");
  }
  return response.status === 204 ? null : response.json();
}

function logout(message = "خارج شدی.") {
  localStorage.removeItem(storage.token);
  elements.dashboard.hidden = true;
  elements.logoutButton.hidden = true;
  setStatus(message, message.includes("معتبر") ? "error" : "");
}

function setStatus(message, mode) {
  elements.status.textContent = message;
  elements.status.classList.toggle("ok", mode === "ok");
  elements.status.classList.toggle("error", mode === "error");
}

function featureSummary(features = {}) {
  const enabled = [];
  if (features.ai) enabled.push("AI کارت");
  if (features.dictionary) enabled.push("دیکشنری");
  if (features.textCorrection) enabled.push("اصلاح");
  if (features.feedbackCards) enabled.push("فیدبک");
  if (features.exportImport) enabled.push("Import");
  if (features.unlimitedCards) enabled.push("نامحدود");
  return enabled.join(" · ") || "محدود";
}

function defaultFeatures() {
  return {
    ai: true,
    dictionary: true,
    textCorrection: true,
    exportImport: true,
    feedbackCards: true,
    unlimitedCards: false
  };
}

function findPlan(nameOrId) {
  return state.plans.find(plan => plan.name === nameOrId || plan.id === nameOrId);
}

function findMetrics(userId) {
  return state.metrics.find(item => item.userId === userId) || {
    totalCards: 0,
    dueCards: 0,
    requestsToday: 0,
    activeMinutesToday: 0,
    cardsAddedToday: 0,
    reviewsToday: 0,
    aiCardToday: 0,
    aiDictionaryToday: 0,
    aiCorrectionToday: 0
  };
}

function findCodeUsage(code) {
  return state.codeUsage.find(item => item.code === code) || { code, usersCount: 0, users: [] };
}

function getUsersPageCount() {
  return Math.max(1, Math.ceil(state.users.length / state.usersPageSize));
}

function changeUsersPage(delta) {
  state.usersPage = Math.min(Math.max(1, state.usersPage + delta), getUsersPageCount());
  renderUsers();
}

function formatAdminDate(value) {
  if (!value) return "-";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "-";
  return new Intl.DateTimeFormat("fa-IR-u-ca-persian", {
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit"
  }).format(date);
}

function safeColor(value, fallback) {
  return /^#[0-9a-f]{6}$/i.test(value || "") ? value : fallback;
}

function slugify(value) {
  return value.toLowerCase().trim().replace(/[^a-z0-9\u0600-\u06ff]+/gi, "-").replace(/^-|-$/g, "") || "plan";
}

function parseTelegramBody(call) {
  if (!call?.body) return null;
  try {
    return JSON.parse(call.body);
  } catch {
    return null;
  }
}

function normalizeUrl(value) {
  return String(value || "").trim().replace(/\/+$/, "");
}

function setButtonLoading(button, loading, text) {
  button.disabled = loading;
  button.textContent = text;
}

function showToast(message) {
  elements.toast.textContent = message;
  elements.toast.classList.add("show");
  clearTimeout(showToast.timer);
  showToast.timer = setTimeout(() => elements.toast.classList.remove("show"), 2600);
}

function toPersianNumber(value) {
  return String(value).replace(/\d/g, digit => "۰۱۲۳۴۵۶۷۸۹"[digit]);
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}
