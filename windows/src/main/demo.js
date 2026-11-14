'use strict';
// Standalone pill: `npx electron src/main/demo.js`. Actions mutate the demo state and send it back.
// NT_SHOT=<folder>: take closed.png and open.png of the pill there, then quit.
const path = require('path');
const fs = require('fs');
const { app } = require('electron');
const { createPill } = require('./pill');
const { demoState } = require('../renderer/demo-state');

const SHOT = process.env.NT_SHOT;
const state = demoState();
let pill = null;

function push() { if (pill) pill.send(state); }

function onAction(name, ...args) {
  const [a0, a1] = args;
  switch (name) {
    case 'expand':
      // screenshots need the notch to stay open with no pointer over it
      if (SHOT && a0 === false) return;
      state.expanded = !!a0;
      console.log('expand', state.expanded);
      break;
    case 'answer':
      state.asks = state.asks.filter((a) => a.id !== a0);
      console.log('answer', a0, JSON.stringify(a1));
      break;
    case 'timer':
      state.timer = a0 == null ? null : { end: Date.now() + a0 * 1000, total: a0 };
      break;
    case 'media':
      if (a0 === 'toggle') state.nowPlaying.playing = !state.nowPlaying.playing;
      else if (a0 === 'seek') state.nowPlaying.position = a1;
      console.log('media', a0, a1 ?? '');
      break;
    case 'shelfAdd':
      state.shelf = [...new Set([...state.shelf, ...a0])];
      break;
    case 'shelfRemove':
      state.shelf = state.shelf.filter((p) => !a0.includes(p));
      break;
    case 'dismissAlert':
      state.alerts = state.alerts.filter((x) => x.id !== a0);
      break;
    case 'dismissToast':
      state.toast = null;
      break;
    default:
      // openPrefs, openURL, openItem, shelfOpen, shelfReveal, shelfPick, quit: the real app acts on these
      console.log('action', name, ...args.map((x) => (typeof x === 'string' ? x : JSON.stringify(x))));
      if (name === 'quit') app.quit();
  }
  push();
}

async function shoot(file, pad = 24) {
  const wc = pill.win.webContents;
  // the pill, plus a card toast under it when there is one
  const r = await wc.executeJavaScript(
    '(() => { const rs = [...document.querySelectorAll("#pill, .cardtoast")].map((e) => e.getBoundingClientRect());' +
    ' const x = Math.min(...rs.map((r) => r.left)), y = Math.min(...rs.map((r) => r.top));' +
    ' const r2 = Math.max(...rs.map((r) => r.right)), b = Math.max(...rs.map((r) => r.bottom));' +
    ' return { x, y, width: r2 - x, height: b - y }; })()');
  const img = await wc.capturePage({
    x: Math.max(0, Math.floor(r.x - pad)), y: Math.max(0, Math.floor(r.y - pad)),
    width: Math.ceil(r.width + pad * 2), height: Math.ceil(r.height + pad * 2),
  });
  fs.writeFileSync(file, img.toPNG());
  console.log('wrote', file, img.getSize());
}

app.whenReady().then(() => {
  pill = createPill({ preload: path.join(__dirname, 'preload.js'), onAction });
  pill.win.webContents.on('console-message', (e, level, msg, line, src) => {
    if (level >= 2) console.log('[pill console]', msg, src ? src.split('/').pop() + ':' + line : '');
  });
  pill.win.webContents.on('render-process-gone', (e, d) => console.log('[pill gone]', d.reason));
  push();
  if (SHOT && process.env.NT_TOAST) {
    // one toast in a given style, shot on its own: island | compact | banner | card | ask | charge | loading
    const kind = process.env.NT_TOAST;
    const style = kind === 'card' ? 'card' : kind === 'compact' ? 'compact' : kind === 'banner' ? 'banner' : 'island';
    state.settings.toastStyle = style;
    state.expanded = false;
    state.toast = {
      id: 't1', symbol: 'battery.100percent.bolt', tint: '#30D158', title: 'Đang sạc · 64%', subtitle: 'Cắm sạc',
      compact: kind === 'compact', action: 'Mở', url: 'https://example.com',
      art: kind === 'charge' ? { kind: 'charge', percent: 64, plugged: true } : kind === 'loading' ? { kind: 'loading' } : null,
      ask: kind === 'ask' ? state.asks[1] : null,
    };
    if (kind === 'ask') state.toast.title = 'Codex đang hỏi';
    push();
    setTimeout(async () => { await shoot(path.join(SHOT, 'toast-' + kind + '.png'), 16); app.quit(); }, 1500);
    return;
  }
  if (!SHOT) return;
  fs.mkdirSync(SHOT, { recursive: true });
  setTimeout(async () => {
    await shoot(path.join(SHOT, 'closed.png'));
    state.expanded = true;
    push();
    setTimeout(async () => {
      await shoot(path.join(SHOT, 'open.png'), 16);
      // the rest of a long open notch: scroll the pill to its end
      await pill.win.webContents.executeJavaScript('document.getElementById("pill").scrollTop = 99999');
      await new Promise((r) => setTimeout(r, 200));
      await shoot(path.join(SHOT, 'open-scrolled.png'), 16);
      app.quit();
    }, 1200);
  }, 1500);
  setTimeout(() => app.exit(1), 20000); // never hang the check
});

app.on('window-all-closed', () => {});
