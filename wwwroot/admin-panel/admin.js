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
  newPlanNameInput: $("#newPlanNameInput"),
  addPlanButton: $("#addPlanButton"),
  plansBoard: $("#plansBoard"),
  usersList: $("#usersList"),
  codePlanInput: $("#codePlanInput"),
  codeMaxUsesInput: $("#codeMaxUsesInput"),
  createCodeButton: $("#createCodeButton"),
  codesList: $("#codesList"),
  toast: $("#toast")
};

let state = { users: [], plans: [], codes: [], settings: null, draggingPlanId: null };

elements.loginForm.addEventListener("submit", adminLogin);
elements.logoutButton.addEventListener("click", logout);
elements.settingsForm.addEventListener("submit", saveSettings);
elements.setWebhookButton.addEventListener("click", setWebhook);
elements.addPlanButton.addEventListener("click", addPlan);
elements.createCodeButton.addEventListener("click", createCode);

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
  const [users, codes, plans, settingsPayload] = await Promise.all([
    adminFetch("/api/admin/users"),
    adminFetch("/api/admin/codes"),
    adminFetch("/api/admin/plans"),
    adminFetch("/api/admin/settings")
  ]);
  state = { ...state, users, codes, plans, settings: settingsPayload.settings };
  elements.dashboard.hidden = false;
  elements.logoutButton.hidden = false;
  setStatus("ادمین تایید شد.", "ok");
  renderStats();
  renderSettings(settingsPayload);
  renderPlans();
  renderUsers();
  renderCodes();
}

function renderStats() {
  elements.usersCount.textContent = toPersianNumber(state.users.length);
  elements.plansCount.textContent = toPersianNumber(state.plans.length);
  elements.codesCount.textContent = toPersianNumber(state.codes.length);
}

function renderSettings(payload) {
  const settings = payload.settings || {};
  const effective = payload.effectiveOpenRouter || {};
  elements.openRouterModelInput.value = settings.openRouterModel || effective.defaultModel || "";
  elements.openRouterRefererInput.value = settings.openRouterReferer || effective.referer || "";
  elements.publicBaseUrlInput.value = settings.publicBaseUrl || "";
  elements.miniAppUrlInput.value = settings.telegramMiniAppUrl || "";
  elements.botUsernameInput.value = settings.telegramBotUsername || "";
  elements.reminderHourInput.value = settings.reminderHour ?? 9;
  elements.botEnabledInput.checked = settings.botEnabled ?? true;
  elements.remindersEnabledInput.checked = settings.remindersEnabled ?? true;
}

function renderPlans() {
  elements.codePlanInput.innerHTML = state.plans.map(plan => `<option value="${escapeHtml(plan.name)}">${escapeHtml(plan.name)}</option>`).join("");
  elements.plansBoard.innerHTML = state.plans.map(plan => `
    <article class="plan-card" draggable="true" data-plan-id="${escapeHtml(plan.id)}">
      <div class="plan-head">
        <strong>${escapeHtml(plan.name)}</strong>
        <span class="plan-grip">↕</span>
      </div>
      <div class="plan-fields">
        <label>نام<input data-field="name" value="${escapeHtml(plan.name)}"></label>
        <label>شناسه<input data-field="id" value="${escapeHtml(plan.id)}" ${plan.isDefault ? "disabled" : ""}></label>
        <label>AI روزانه<input data-field="aiDailyLimit" type="number" value="${plan.aiDailyLimit}"></label>
        <label>AI ماهانه<input data-field="aiMonthlyLimit" type="number" value="${plan.aiMonthlyLimit}"></label>
        <label>سقف کارت<input data-field="cardLimit" type="number" value="${plan.cardLimit}"></label>
        <label>ترتیب<input data-field="sortOrder" type="number" value="${plan.sortOrder}"></label>
      </div>
      <div class="feature-line">
        ${featureCheckbox("ai", "AI", plan.features.ai)}
        ${featureCheckbox("feedbackCards", "Feedback", plan.features.feedbackCards)}
        ${featureCheckbox("exportImport", "Import", plan.features.exportImport)}
        ${featureCheckbox("unlimitedCards", "Unlimited", plan.features.unlimitedCards)}
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
  elements.usersList.innerHTML = state.users.map(user => `
    <article class="admin-item">
      <div>
        <strong>${escapeHtml(user.displayName || user.id)}</strong>
        <small>${escapeHtml(user.id)} · tg:${escapeHtml(user.telegramId || "-")} · @${escapeHtml(user.telegramUsername || "-")}</small>
        <small>${escapeHtml(user.source)} · ${escapeHtml(user.plan)} · ${user.isActive ? "فعال" : "غیرفعال"} · AI: ${escapeHtml(featureSummary(user.features))}</small>
      </div>
      <div class="user-controls">
        <select data-user-field="plan">${state.plans.map(plan => `<option value="${escapeHtml(plan.name)}" ${plan.name === user.plan ? "selected" : ""}>${escapeHtml(plan.name)}</option>`).join("")}</select>
        <button class="mini-button" type="button" data-action="toggle-active">${user.isActive ? "غیرفعال" : "فعال"}</button>
        <button class="mini-button" type="button" data-action="toggle-reminder">${user.remindersEnabled ? "قطع یادآوری" : "فعال یادآوری"}</button>
      </div>
    </article>
  `).join("");

  elements.usersList.querySelectorAll(".admin-item").forEach((item, index) => {
    const user = state.users[index];
    item.querySelector('[data-user-field="plan"]').addEventListener("change", event => updateUser(user.id, { plan: event.target.value }));
    item.querySelector('[data-action="toggle-active"]').addEventListener("click", () => updateUser(user.id, { isActive: !user.isActive }));
    item.querySelector('[data-action="toggle-reminder"]').addEventListener("click", () => updateUser(user.id, { remindersEnabled: !user.remindersEnabled }));
  });
}

function renderCodes() {
  elements.codesList.innerHTML = state.codes.map(code => `
    <article class="admin-item">
      <div>
        <strong>${escapeHtml(code.code)}</strong>
        <small>${escapeHtml(code.plan)} · ${toPersianNumber(code.uses)}/${toPersianNumber(code.maxUses)}</small>
      </div>
    </article>
  `).join("");
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
  const result = await adminFetch("/api/admin/bot/set-webhook", { method: "POST" });
  showToast(result.ok ? "Webhook تنظیم شد." : result.message || "Webhook تنظیم نشد.");
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
      features: { ai: true, exportImport: true, feedbackCards: true, unlimitedCards: false },
      aiDailyLimit: 20,
      aiMonthlyLimit: 300,
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
  const plan = state.plans.find(item => item.name === planName) || state.plans[0];
  const code = await adminFetch("/api/admin/codes", {
    method: "POST",
    body: JSON.stringify({
      plan: plan?.name || "Free",
      maxUses: Number(elements.codeMaxUsesInput.value || 1),
      features: plan?.features || { ai: true, exportImport: true, feedbackCards: true, unlimitedCards: true }
    })
  });
  showToast(`کد ساخته شد: ${code.code}`);
  await loadAll();
}

function readPlanCard(card) {
  const get = field => card.querySelector(`[data-field="${field}"]`);
  return {
    id: get("id").value.trim(),
    name: get("name").value.trim(),
    aiDailyLimit: Number(get("aiDailyLimit").value),
    aiMonthlyLimit: Number(get("aiMonthlyLimit").value),
    cardLimit: Number(get("cardLimit").value),
    sortOrder: Number(get("sortOrder").value),
    isDefault: get("isDefault").checked,
    features: {
      ai: get("feature-ai").checked,
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
  if (features.ai) enabled.push("AI");
  if (features.feedbackCards) enabled.push("Feedback");
  if (features.exportImport) enabled.push("Import");
  if (features.unlimitedCards) enabled.push("Unlimited");
  return enabled.join(" · ") || "محدود";
}

function slugify(value) {
  return value.toLowerCase().trim().replace(/[^a-z0-9\u0600-\u06ff]+/gi, "-").replace(/^-|-$/g, "") || "plan";
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
