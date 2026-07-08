(() => {
  const DB_NAME = "lingualite-offline";
  const DB_VERSION = 1;
  const SNAPSHOTS = "snapshots";
  const OPERATIONS = "operations";
  const accountStorageKey = "lingualite.offlineAccount";

  function openDatabase() {
    return new Promise((resolve, reject) => {
      const request = indexedDB.open(DB_NAME, DB_VERSION);
      request.onupgradeneeded = () => {
        const db = request.result;
        if (!db.objectStoreNames.contains(SNAPSHOTS)) {
          db.createObjectStore(SNAPSHOTS, { keyPath: "account" });
        }
        if (!db.objectStoreNames.contains(OPERATIONS)) {
          const store = db.createObjectStore(OPERATIONS, { keyPath: "id" });
          store.createIndex("account_createdAt", ["account", "createdAt"]);
        }
      };
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  }

  async function run(storeName, mode, action) {
    const db = await openDatabase();
    return new Promise((resolve, reject) => {
      const transaction = db.transaction(storeName, mode);
      const store = transaction.objectStore(storeName);
      let result;
      transaction.oncomplete = () => {
        db.close();
        resolve(result);
      };
      transaction.onerror = () => {
        db.close();
        reject(transaction.error);
      };
      transaction.onabort = transaction.onerror;
      action(store, (value) => { result = value; });
    });
  }

  function currentAccount() {
    return localStorage.getItem(accountStorageKey) || "";
  }

  function setAccount(account) {
    if (account) localStorage.setItem(accountStorageKey, account);
  }

  async function saveSnapshot(snapshot, account = currentAccount()) {
    if (!account) return;
    await run(SNAPSHOTS, "readwrite", (store) => {
      store.put({ account, savedAt: new Date().toISOString(), data: snapshot });
    });
  }

  async function loadSnapshot(account = currentAccount()) {
    if (!account) return null;
    return run(SNAPSHOTS, "readonly", (store, done) => {
      const request = store.get(account);
      request.onsuccess = () => done(request.result?.data || null);
    });
  }

  async function enqueue(operation, account = currentAccount()) {
    if (!account) throw new Error("حساب آفلاین هنوز آماده نشده است.");
    const item = {
      ...operation,
      id: operation.id || crypto.randomUUID(),
      account,
      createdAt: operation.createdAt || Date.now()
    };
    await run(OPERATIONS, "readwrite", (store) => store.put(item));
    return item;
  }

  async function pending(account = currentAccount()) {
    if (!account) return [];
    const all = await run(OPERATIONS, "readonly", (store, done) => {
      const request = store.getAll();
      request.onsuccess = () => done(request.result || []);
    });
    return all
      .filter((item) => item.account === account)
      .sort((left, right) => left.createdAt - right.createdAt);
  }

  async function remove(id) {
    await run(OPERATIONS, "readwrite", (store) => store.delete(id));
  }

  window.LinguaOffline = {
    currentAccount,
    setAccount,
    saveSnapshot,
    loadSnapshot,
    enqueue,
    pending,
    remove
  };
})();
