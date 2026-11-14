'use strict';
// The pill: one frameless, transparent window at the top centre of the primary display, sized once to the largest
// open size. It ignores the mouse everywhere except over the pill's own rectangle, which the page reports.
const path = require('path');
const { BrowserWindow, screen, ipcMain, app, nativeImage } = require('electron');

const W = 760, H = 600;
const HOVER_MS = 120;   // cursor over the closed pill this long opens it (hover mode)
const LEAVE_MS = 350;   // cursor outside the open pill this long closes it
const NEAR = 40;        // px around the pill where the cursor is watched at 10 Hz, and 5 Hz elsewhere

function createPill({ preload, onAction }) {
  let expanded = false, rect = null, hold = false, ready = false, destroyed = false;
  let last = null, ignoring = true, enterAt = 0, leaveAt = 0, pollTimer = null;

  const win = new BrowserWindow({
    width: W, height: H, frame: false, transparent: true, resizable: false, movable: false,
    skipTaskbar: true, alwaysOnTop: true, focusable: false, hasShadow: false, show: false,
    backgroundColor: '#00000000',
    webPreferences: { preload, contextIsolation: true, sandbox: false, nodeIntegration: false },
  });
  const mine = (e) => e.sender === win.webContents;

  function place() {
    const wa = screen.getPrimaryDisplay().workArea;
    win.setBounds({ x: Math.round(wa.x + (wa.width - W) / 2), y: wa.y, width: W, height: H });
  }
  place();
  win.setAlwaysOnTop(true, 'screen-saver');
  win.setIgnoreMouseEvents(true, { forward: true });
  win.loadFile(path.join(__dirname, '../renderer/pill.html'));
  win.webContents.once('did-finish-load', () => { ready = true; if (last) win.webContents.send('state', last); });
  win.once('ready-to-show', () => win.showInactive());

  // the page's expand clicks and the pill's own hover decisions both reach the caller through onAction('expand')
  function setExpanded(v) {
    if (v === expanded) return;
    expanded = v;
    onAction('expand', v);
  }

  const onRect = (e, r) => { if (mine(e)) rect = r && r.width > 0 ? r : null; };
  const onHold = (e, on) => { if (mine(e)) hold = !!on; };
  // the page's mouse moves: look at the cursor now instead of at the next poll
  const onPoke = (e) => { if (mine(e) && !destroyed) { clearTimeout(pollTimer); tick(); } };
  const onAct = (e, name, args) => {
    if (!mine(e)) return;
    if (name === 'expand') setExpanded(!!args[0]);
    else onAction(name, ...args);
  };
  // dragging a shelf file out of the pill: a native drag carrying the file's own icon
  const onDrag = async (e, paths) => {
    if (!mine(e) || !Array.isArray(paths) || !paths.length) return;
    let icon;
    try { icon = await app.getFileIcon(paths[0], { size: 'normal' }); } catch { icon = nativeImage.createEmpty(); }
    e.sender.startDrag({ file: paths[0], files: paths, icon });
  };
  ipcMain.on('pill-rect', onRect);
  ipcMain.on('pill-hold', onHold);
  ipcMain.on('pill-poke', onPoke);
  ipcMain.on('act', onAct);
  ipcMain.on('shelfDrag', onDrag);
  screen.on('display-metrics-changed', place);
  screen.on('display-added', place);
  screen.on('display-removed', place);

  // Main decides hover and leave from the real cursor: no per-frame work, no reliance on DOM mouse events.
  function tick() {
    if (destroyed) return;
    const now = Date.now();
    let inside = false, near = false;
    try {
      const p = screen.getCursorScreenPoint(), b = win.getBounds();
      const x = p.x - b.x, y = p.y - b.y;
      if (rect) {
        inside = x >= rect.x && x < rect.x + rect.width && y >= rect.y && y < rect.y + rect.height;
        near = x >= rect.x - NEAR && x < rect.x + rect.width + NEAR && y >= rect.y - NEAR && y < rect.y + rect.height + NEAR;
      }
    } catch { /* display gone: treat as outside */ }

    if (inside === ignoring) {
      ignoring = !inside;
      win.setIgnoreMouseEvents(ignoring, ignoring ? { forward: true } : undefined);
    }
    const mode = last && last.settings ? last.settings.expandMode : 'hover';
    if (inside && !expanded && mode !== 'click') {
      enterAt = enterAt || now;
      if (now - enterAt >= HOVER_MS) { enterAt = 0; setExpanded(true); }
    } else enterAt = 0;

    if (expanded && !inside && !hold) {
      leaveAt = leaveAt || now;
      if (now - leaveAt >= LEAVE_MS) { leaveAt = 0; setExpanded(false); }
    } else leaveAt = 0;

    pollTimer = setTimeout(tick, expanded || near ? 100 : 200);
  }
  tick();

  function send(state) {
    if (destroyed) return;
    last = state;
    if (typeof state.expanded === 'boolean') expanded = state.expanded;
    if (ready) win.webContents.send('state', state);
  }

  function destroy() {
    if (destroyed) return;
    destroyed = true;
    clearTimeout(pollTimer);
    ipcMain.removeListener('pill-rect', onRect);
    ipcMain.removeListener('pill-hold', onHold);
    ipcMain.removeListener('pill-poke', onPoke);
    ipcMain.removeListener('act', onAct);
    ipcMain.removeListener('shelfDrag', onDrag);
    screen.removeListener('display-metrics-changed', place);
    screen.removeListener('display-added', place);
    screen.removeListener('display-removed', place);
    if (!win.isDestroyed()) win.destroy();
  }

  // the pointer is over the pill's rectangle (main keeps toasts up while it is)
  return { win, send, destroy, hovered: () => !ignoring && rect !== null };
}

module.exports = { createPill };
