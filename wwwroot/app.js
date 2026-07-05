const tg = window.Telegram?.WebApp;
tg?.ready();
tg?.expand();

if (tg?.themeParams) {
  const root = document.documentElement;
  const theme = tg.themeParams;
  if (theme.bg_color) root.style.setProperty("--bg", theme.bg_color);
  if (theme.text_color) root.style.setProperty("--text", theme.text_color);
  if (theme.hint_color) root.style.setProperty("--muted", theme.hint_color);
  if (theme.button_color) root.style.setProperty("--primary", theme.button_color);
  if (theme.secondary_bg_color) root.style.setProperty("--surface-soft", theme.secondary_bg_color);
}

const state = {
  due: [],
  current: null,
  summary: null
};

const elements = {
  dueCards: document.querySelector("#dueCards"),
  totalCards: document.querySelector("#totalCards"),
  accuracy: document.querySelector("#accuracy"),
  cardType: document.querySelector("#cardType"),
  boxLabel: document.querySelector("#boxLabel"),
  boxProgress: document.querySelector("#boxProgress"),
  frontText: document.querySelector("#frontText"),
  exampleText: document.querySelector("#exampleText"),
  backText: document.querySelector("#backText"),
  qaText: document.querySelector("#qaText"),
  answerPanel: document.querySelector("#answerPanel"),
  reviewCard: document.querySelector("#reviewCard"),
  emptyState: document.querySelector("#emptyState"),
  revealButton: document.querySelector("#revealButton"),
  rememberedButton: document.querySelector("#rememberedButton"),
  forgotButton: document.querySelector("#forgotButton"),
  refreshButton: document.querySelector("#refreshButton"),
  form: document.querySelector("#cardForm"),
  boxes: document.querySelector("#boxes"),
  deckList: document.querySelector("#deckList"),
  toast: document.querySelector("#toast")
};

const typeLabels = {
  Word: "لغت",
  Sentence: "جمله",
  Question: "پرسش"
};

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

loadAll();

async function loadAll() {
  try {
    const [summary, due, cards] = await Promise.all([
      fetchJson("/api/deck"),
      fetchJson("/api/cards/due"),
      fetchJson("/api/cards")
    ]);

    state.summary = summary;
    state.due = due;
    updateSummary(summary);
    renderBoxes(summary.boxes);
    renderDeck(cards);
    pickNextCard();
  } catch (error) {
    showToast(error.message || "مشکلی در دریافت اطلاعات پیش آمد.");
  }
}

function switchView(viewName) {
  document.querySelectorAll(".tab").forEach((tab) => {
    tab.classList.toggle("active", tab.dataset.view === viewName);
  });

  document.querySelectorAll(".view").forEach((view) => {
    view.classList.toggle("active", view.id === `${viewName}View`);
  });

  if (viewName === "deck") {
    loadAll();
  }
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
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ remembered })
    });

    tg?.HapticFeedback?.notificationOccurred(remembered ? "success" : "warning");
    showToast(remembered ? "عالی، رفت جعبه بعدی." : "اشکالی ندارد، دوباره مرور می‌شود.");
    await refreshSummaryOnly();
    pickNextCard();
  } catch (error) {
    showToast(error.message || "ثبت مرور انجام نشد.");
  }
}

async function createCard(event) {
  event.preventDefault();
  const formData = new FormData(elements.form);
  const payload = Object.fromEntries(formData.entries());

  try {
    await fetchJson("/api/cards", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });

    elements.form.reset();
    showToast("کارت جدید اضافه شد.");
    tg?.HapticFeedback?.notificationOccurred("success");
    await loadAll();
    switchView("review");
  } catch (error) {
    showToast(error.message || "کارت ذخیره نشد.");
  }
}

async function refreshSummaryOnly() {
  const summary = await fetchJson("/api/deck");
  state.summary = summary;
  updateSummary(summary);
  renderBoxes(summary.boxes);
}

function setBoxProgress(box) {
  elements.boxProgress.querySelectorAll("i").forEach((item, index) => {
    item.classList.toggle("active", index < box);
  });
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
  elements.deckList.innerHTML = "";
  if (!cards.length) {
    elements.deckList.innerHTML = `<div class="empty-state"><strong>هنوز کارتی نداری.</strong><span>از بخش افزودن شروع کن.</span></div>`;
    return;
  }

  for (const card of cards) {
    const item = document.createElement("article");
    item.className = "deck-item";
    item.innerHTML = `
      <h3>${escapeHtml(card.front)}</h3>
      <p>${escapeHtml(card.back)}</p>
      <footer>
        <span>${typeLabels[card.type] ?? "کارت"}</span>
        <span>جعبه ${toPersianNumber(card.box)}</span>
      </footer>
    `;
    elements.deckList.appendChild(item);
  }
}

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    throw new Error(error.message || "درخواست ناموفق بود.");
  }

  if (response.status === 204) return null;
  return response.json();
}

function showToast(message) {
  elements.toast.textContent = message;
  elements.toast.classList.add("show");
  clearTimeout(showToast.timer);
  showToast.timer = setTimeout(() => elements.toast.classList.remove("show"), 2400);
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
