'use strict';
const pendingContainer = document.getElementById('pending');
const trustedContainer = document.getElementById('trusted');
const allowAll = document.getElementById('allowAll');
const status = document.getElementById('status');

function node(tag, text, className) {
  const el = document.createElement(tag);
  if (text !== undefined) el.textContent = text;
  if (className) el.className = className;
  return el;
}

function card(item, label, action) {
  const row = node('div', undefined, 'item');
  const info = node('div', undefined, 'info');
  info.append(node('strong', item.name || 'Unknown application'), node('div', item.path || '', 'path'));
  row.append(info);
  const button = node('button', label, label === 'Approve' ? 'primary' : '');
  button.addEventListener('click', action);
  row.append(button);
  return row;
}

async function render() {
  const { trustedClients = [], pendingClients = [], allowAllClients = false } =
    await chrome.storage.local.get(['trustedClients', 'pendingClients', 'allowAllClients']);
  allowAll.checked = allowAllClients;
  pendingContainer.replaceChildren();
  trustedContainer.replaceChildren();
  for (const item of pendingClients) {
    const row = card(item, 'Approve', async () => {
      const current = await chrome.storage.local.get({ trustedClients: [], pendingClients: [] });
      const approved = [...current.trustedClients.filter(c => c.id !== item.id),
        { id: item.id, name: item.name, path: item.path, approvedAt: Date.now() }];
      await chrome.storage.local.set({ trustedClients: approved,
        pendingClients: current.pendingClients.filter(c => c.id !== item.id) });
      status.textContent = 'Application approved. Retry playback in Flyleaf.';
      await render();
    });
    const reject = node('button', 'Dismiss');
    reject.addEventListener('click', async () => {
      const { pendingClients: current = [] } = await chrome.storage.local.get('pendingClients');
      await chrome.storage.local.set({ pendingClients: current.filter(c => c.id !== item.id) });
      await render();
    });
    row.append(reject);
    pendingContainer.append(row);
  }
  for (const item of trustedClients)
    trustedContainer.append(card(item, 'Revoke', async () => {
      const { trustedClients: current = [] } = await chrome.storage.local.get('trustedClients');
      await chrome.storage.local.set({ trustedClients: current.filter(c => c.id !== item.id) });
      await render();
    }));
  if (!pendingClients.length) pendingContainer.append(node('p', 'No pending requests.', 'empty'));
  if (!trustedClients.length) trustedContainer.append(node('p', 'No trusted applications.', 'empty'));
}

document.getElementById('reconnect').addEventListener('click', () => chrome.runtime.reload());

allowAll.addEventListener('change', async () => {
  if (allowAll.checked && !confirm('Allow ANY local application to request browser cookies?')) {
    allowAll.checked = false;
    return;
  }
  await chrome.storage.local.set({ allowAllClients: allowAll.checked });
});
chrome.storage.onChanged.addListener(() => { void render(); });
void render();
