'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../BrowserExtension/service-worker.js'), 'utf8');

function createHarness(privateMode = false) {
  const calls = { getCookies: 0, settingsOpened: 0, listeners: {} };
  const state = { trustedClients: [], allowAllClients: false, pendingClients: [] };
  const messages = [];
  let portReceive;
  const port = { onMessage: { addListener(fn) { portReceive = fn; } },
    onDisconnect: { addListener() {} },
    postMessage(message) { messages.push(message); }, disconnect() {} };
  const chrome = {
    extension: { inIncognitoContext: privateMode },
    windows: { getAll: async () => privateMode ? [{ incognito: true }] : [{ incognito: false }],
      onCreated: { addListener() {} }, onRemoved: { addListener() {} } },
    cookies: { getAll: async () => { calls.getCookies++; return [
      { name: 'session', value: 'secret', domain: '.example.org', path: '/',
        hostOnly: false, secure: true, httpOnly: true, session: true },
      { name: 'partitioned', value: 'omit', domain: '.example.org', path: '/',
        partitionKey: { topLevelSite: 'https://elsewhere.net' } }]; } },
    storage: { local: { get: async (defaults) => {
      if (typeof defaults === 'string') return { [defaults]: state[defaults] };
      const keys = Array.isArray(defaults) ? defaults : Object.keys(defaults);
      const output = { ...(Array.isArray(defaults) ? {} : defaults) };
      for (const key of keys) if (key in state) output[key] = state[key];
      return output;
    }, set: async data => Object.assign(state, data) } },
    runtime: { connectNative: () => port, openOptionsPage: async () => { calls.settingsOpened++; },
      onInstalled: { addListener() {} }, onStartup: { addListener() {} } }
  };
  const scope = { chrome, navigator: {}, URL, setTimeout, Date };
  vm.runInNewContext(source, scope);
  const tick = () => new Promise(r => setTimeout(r, 20));
  return { calls, state, messages, async send(msg) { await tick(); portReceive(msg); await tick(); }, tick };
}

(async () => {
  for (const privateMode of [false, true]) {
    const h = createHarness(privateMode);
    await h.tick();
    assert.equal(h.messages[0].type, 'hello');
    assert.equal(h.messages[0].protocol, 3);
    assert.equal(h.messages[0].mode, privateMode ? 'private' : 'normal');
    const client = { id: 'a'.repeat(64), name: 'Player.exe', path: 'C:\\Apps\\Player.exe' };
    const msg = { protocol: 3, type: 'getCookies', requestId: '12345678',
      url: 'https://example.org/', client };
    await h.send(msg);
    assert.equal(h.messages.at(-1).error, 'pairing_required');
    assert.equal(h.calls.getCookies, 0, 'no cookie access before authorization');
    assert.equal(h.state.pendingClients.length, 1);
    assert.equal(h.calls.settingsOpened, 1);
    await h.send(msg);
    assert.equal(h.calls.settingsOpened, 1, 'no repetitive settings tabs');
    h.state.trustedClients.push(client);
    await h.send(msg);
    assert.equal(h.messages.at(-1).ok, true);
    assert.equal(h.messages.at(-1).cookies.length, 1, 'partitioned cookie must not leak');
    assert.equal(h.messages.at(-1).cookies[0].httpOnly, true);
    h.state.trustedClients = [];
    h.state.allowAllClients = true;
    await h.send(msg);
    assert.equal(h.messages.at(-1).ok, true, 'explicit developer override');
  }
  console.log('extension pairing / cookie isolation tests passed');
})().catch(e => { console.error(e); process.exitCode = 1; });
