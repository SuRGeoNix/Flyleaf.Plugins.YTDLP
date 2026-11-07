'use strict';

// One service worker per normal/private browser context. Trust records are
// shared through chrome.storage.local; cookie stores are NOT shared.
const HOST = 'com.flyleaf.browsercookies';
const PROTOCOL = 3;
const mode = chrome.extension.inIncognitoContext ? 'private' : 'normal';
let port = null;
let connecting = false;
let retryCount = 0;

async function browserName() {
  try {
    if (navigator.brave?.isBrave && await navigator.brave.isBrave()) return 'brave';
    if (navigator.userAgentData?.brands?.some(x => x.brand.toLowerCase().includes('brave')))
      return 'brave';
  } catch (_) { /* Chromium does not expose Brave identity reliably. */ }
  return 'chrome';
}

async function hasPrivateWindow() {
  if (mode !== 'private') return true;
  const windows = await chrome.windows.getAll({ windowTypes: ['normal', 'popup'] });
  return windows.some(w => w.incognito);
}

function validClient(client) {
  return client && /^[0-9a-f]{64}$/.test(client.id || '') &&
    typeof client.name === 'string' && client.name.length <= 260 &&
    typeof client.path === 'string' && client.path.length <= 2048;
}

async function authorized(client) {
  if (!validClient(client)) return false;
  const state = await chrome.storage.local.get({ trustedClients: [], allowAllClients: false, pendingClients: [] });
  if (state.allowAllClients || state.trustedClients.some(c => c.id === client.id)) return true;

  const pending = state.pendingClients.filter(c => c.id !== client.id &&
    Date.now() - c.requestedAt < 24 * 60 * 60 * 1000);
  pending.push({ id: client.id, name: client.name, path: client.path, requestedAt: Date.now() });
  await chrome.storage.local.set({ pendingClients: pending });
  // One-time user action; never prompt again while this client remains pending.
  if (!state.pendingClients.some(c => c.id === client.id)) {
    try { await chrome.runtime.openOptionsPage(); }
    catch (_) { /* The settings page can always be opened manually. */ }
  }
  return false;
}

async function getCookies(url) {
  const parsed = new URL(url);
  if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error('invalid_url');
  if (!await hasPrivateWindow()) throw new Error('private_session_closed');
  const entries = await chrome.cookies.getAll({ url: parsed.href });
  // Netscape cookies/FFmpeg cannot represent partitioned cookies faithfully.
  return entries.filter(c => !c.partitionKey).map(c => ({
    name: c.name, value: c.value, domain: c.domain, path: c.path,
    hostOnly: c.hostOnly, secure: c.secure, httpOnly: c.httpOnly,
    session: c.session, expirationDate: c.expirationDate ?? null
  }));
}

function postQuietly(activePort, message) {
  try { activePort.postMessage({ protocol: PROTOCOL, ...message }); }
  catch (_) { /* Browser disconnected. */ }
}

function handleMessage(message, activePort) {
  if (!message || message.protocol !== PROTOCOL || message.type !== 'getCookies' ||
      typeof message.requestId !== 'string') return;
  (async () => {
    try {
      if (!await authorized(message.client)) {
        postQuietly(activePort, { type: 'cookies', requestId: message.requestId,
          ok: false, error: 'pairing_required' });
        return;
      }
      const cookies = await getCookies(message.url);
      postQuietly(activePort, { type: 'cookies', requestId: message.requestId, ok: true, cookies });
    } catch (_) {
      postQuietly(activePort, { type: 'cookies', requestId: message.requestId,
        ok: false, error: 'cookie_query_failed' });
    }
  })();
}

async function connect() {
  if (connecting || port) return;
  connecting = true;
  try {
    if (!await hasPrivateWindow()) return;
    const current = chrome.runtime.connectNative(HOST);
    port = current;
    current.onMessage.addListener(message => handleMessage(message, current));
    current.onDisconnect.addListener(() => {
      if (port !== current) return;
      const error = chrome.runtime.lastError?.message || '';
      port = null;
      if (/native messaging host.*not found|specified native messaging host/i.test(error)) return;
      if (retryCount++ < 2) setTimeout(() => { void connect(); }, 1500);
    });
    current.postMessage({ protocol: PROTOCOL, type: 'hello', browser: await browserName(), mode });
  } catch (_) { /* Host missing, or browser is closing. */ }
  finally { connecting = false; }
}

chrome.runtime.onInstalled.addListener(() => { void connect(); });
chrome.runtime.onStartup.addListener(() => { void connect(); });
chrome.windows.onCreated.addListener(window => {
  if (!!window.incognito === (mode === 'private')) void connect();
});
chrome.windows.onRemoved.addListener(() => {
  if (mode !== 'private') return;
  void hasPrivateWindow().then(open => {
    if (open || !port) return;
    const old = port;
    port = null;
    old.disconnect();
  }).catch(() => {});
});
void connect();
