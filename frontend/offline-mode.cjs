'use strict';
// G HUB 2025.9 offline policy. Local device IPC and bundled resources stay intact.
const {app, session, shell, ipcMain} = require('electron');
const fs = require('node:fs');
const started = Date.now();
for (const key of Object.keys(process.env)) {
  if (/^(https?|all|ftp|no)_proxy$/i.test(key)) delete process.env[key];
}
for (const flag of ['no-proxy-server', 'disable-background-networking', 'disable-component-update', 'disable-domain-reliability']) {
  app.commandLine.appendSwitch(flag);
}
function isLoopback(host) {
  const h = host.toLowerCase().replace(/^\[|\]$/g, '');
  return h === 'localhost' || h === '::1' || /^127(?:\.\d{1,3}){3}$/.test(h);
}
function isLocalFile(url) {
  if (url.protocol !== 'file:' || (url.hostname && url.hostname.toLowerCase() !== 'localhost')) return false;
  try {
    const encodedPath = url.pathname;
    const decodedPath = decodeURIComponent(encodedPath);
    return [encodedPath, decodedPath].every(path => /^\/[A-Za-z]:\//.test(path) && !/%(?:2f|5c)/i.test(path) && !path.includes('\\'));
  } catch { return false; }
}
function allowedRequest(input) {
  try {
    const url = new URL(typeof input === 'string' ? input : input.url || input.href);
    if (url.protocol === 'file:') return isLocalFile(url);
    if (['http:', 'https:', 'ws:', 'wss:', 'ftp:'].includes(url.protocol)) return isLoopback(url.hostname);
    return ['data:', 'blob:', 'about:', 'chrome:', 'devtools:', 'chrome-devtools:', 'sentry-ipc:'].includes(url.protocol);
  } catch { return false; }
}
function bench(stage) {
  if (!process.env.GHUB_OFFLINE_BENCH_LOG) return;
  try { fs.appendFileSync(process.env.GHUB_OFFLINE_BENCH_LOG, JSON.stringify({pid:process.pid, stage, ms:Date.now()-started, time:new Date().toISOString()})+'\n'); } catch {}
}
const guardedSessions = new WeakSet();
function guardSession(s) {
  if (guardedSessions.has(s)) return;
  guardedSessions.add(s);
  s.webRequest.onBeforeRequest({urls:['<all_urls>']}, (details, done) => done({cancel:!allowedRequest(details.url)}));
}
app.on('session-created', guardSession);
app.once('ready', () => {guardSession(session.defaultSession); bench('ready');});
const originalFetch = globalThis.fetch;
if (typeof originalFetch === 'function') globalThis.fetch = function(input, options) {
  if (!allowedRequest(input)) return Promise.reject(Object.assign(new Error('G HUB offline mode: external request blocked'), {code:'GHUB_OFFLINE'}));
  return originalFetch.call(this, input, options);
};
const originalOpenExternal = shell.openExternal.bind(shell);
shell.openExternal = function(url, options) {
  // Prevent cloud links from outsourcing network access to the default browser.
  try {if (!isLocalFile(new URL(url))) return Promise.resolve();} catch {return Promise.resolve();}
  return originalOpenExternal(url, options);
};
app.on('browser-window-created', (_, window) => {
  bench('window-created');
  window.webContents.once('did-finish-load', () => bench('page-loaded'));
});
const originalHandle = ipcMain.handle.bind(ipcMain);
ipcMain.handle = function(channel, listener) {
  if (channel === 'ON_APP_LOADED') return originalHandle(channel, function(...args) {bench('device-ui-ready'); return listener.apply(this,args);});
  return originalHandle(channel, listener);
};
bench('offline-policy-loaded');
module.exports = {allowedRequest};
