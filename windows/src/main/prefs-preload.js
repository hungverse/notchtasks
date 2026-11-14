'use strict';
// Preload for prefs.html and onboarding.html: the window.nt bridge. Nothing here changes settings itself: every
// change is an act(name, ...args) that main handles (setSetting, installHook, pair, …).
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { pathToFileURL } = require('url');
const { contextBridge, ipcRenderer } = require('electron');
const l10n = require('../core/l10n');
const { resourceDir } = require('../core/resources');
const S = require('../core/settings');
const watch = require('../core/watchitems');
const devscan = require('../core/devscan');

const DIRS = ipcRenderer.sendSync('prefs:dirs');
const IMAGE_EXT = new Set(['.png', '.jpg', '.jpeg', '.gif', '.webp', '.bmp', '.heic']);
const SOUND_EXT = new Set(['.wav', '.mp3', '.m4a', '.ogg', '.aiff', '.aif']);
const MAX_SOUND = 8_000_000;

// Copies a file the user picked into the app's folder, so deleting the original never breaks the icon or sound.
function keepCopy(dir, src, exts, maxBytes) {
  try {
    const ext = path.extname(src).toLowerCase();
    if (!exts.has(ext) || fs.statSync(src).size > maxBytes) return null;
    fs.mkdirSync(dir, { recursive: true });
    const dest = path.join(dir, `${crypto.randomUUID()}${ext}`);
    fs.copyFileSync(src, dest);
    return dest;
  } catch { return null; }
}

const stateCallbacks = [];
const paneCallbacks = [];
ipcRenderer.on('prefs:state', (_e, state) => {
  // the language is chosen in the app: the same choice here, so nt.L speaks it
  l10n.setChoice(state.choice || '');
  for (const fn of stateCallbacks) fn(state);
});
ipcRenderer.on('prefs:pane', (_e, pane) => { for (const fn of paneCallbacks) fn(pane); });

// The same mapping as the pill (icons.js): a settings symbol is an SF Symbol name, drawn as its Lucide file. The page's
// own chrome icons are Lucide names, drawn as they are when icons.js has no entry for them.
const ICONS = require('../renderer/icons');
const LUCIDE_DIR = path.join(path.dirname(require.resolve('lucide-static/package.json')), 'icons');
const svgCache = new Map();
function iconSVG(name) {
  const sf = String(name || '');
  if (svgCache.has(sf)) return svgCache.get(sf);
  const base = ICONS[sf] || ICONS[sf.replace(/\.fill$/, '')] || sf;
  let svg = '';
  try { svg = fs.readFileSync(path.join(LUCIDE_DIR, `${base}.svg`), 'utf8'); } catch {
    try { svg = fs.readFileSync(path.join(LUCIDE_DIR, 'circle.svg'), 'utf8'); } catch { svg = ''; }
  }
  svgCache.set(sf, svg);
  return svg;
}
// The symbol picker lists the SF names icons.js knows, so a picked name is one the phone and the pill draw the same.
const iconNames = () => Object.keys(ICONS).sort();

const sprites = (() => {
  try { return JSON.parse(fs.readFileSync(path.join(resourceDir('Sprites'), 'catalog.json'), 'utf8')); } catch { return []; }
})();

function catalog() {
  return {
    modules: S.modulesAvailable().map((id) => ({ id, ...S.MODULE_INFO[id] })),
    moduleGroups: S.MODULE_GROUPS.map((g) => ({ title: g.title, ids: g.ids.filter((id) => S.modulesAvailable().includes(id)) })),
    expandModes: S.EXPAND_MODES, sizes: S.SIZES, backgrounds: S.BACKGROUNDS, toastStyles: S.TOAST_STYLES, motions: S.MOTIONS,
    shapes: S.SHAPES, placements: S.PLACEMENTS, perSide: S.PER_SIDE, proPerSide: S.PRO_PER_SIDE,
    accents: S.ACCENTS, soundNames: S.SOUND_NAMES, soundEvents: S.SOUND_EVENTS, soundPacks: S.SOUND_PACKS, themes: S.THEMES,
    presetGroups: watch.presetGroups.map((g) => ({ title: g.title, items: g.items })),
    buildPresets: watch.buildPresets,
    knownTools: devscan.KNOWN_TOOLS,
    sprites: sprites.map((s) => ({ id: s.id, title: s.title, category: s.category, frames: s.frames, fps: s.fps, pixel: !!s.pixel })),
    iconNames: iconNames(),
    languages: l10n.languages,
  };
}

contextBridge.exposeInMainWorld('nt', {
  platform: process.platform,
  onState: (cb) => stateCallbacks.push(cb),
  onPane: (cb) => paneCallbacks.push(cb),
  act: (name, ...args) => ipcRenderer.send('prefs:act', name, args),
  L: (key, ...args) => l10n.L(key, ...args),
  icon: (name) => iconSVG(name),
  iconNames: () => iconNames(),
  spriteURL: (id) => pathToFileURL(path.join(resourceDir('Sprites'), `${id}.png`)).href,
  fileURL: (p) => pathToFileURL(p).href,
  /** Absolute path of the copy, or null when the file is not a picture (or too big as a sound). */
  importImage: (src) => keepCopy(DIRS.badges, src, IMAGE_EXT, 50_000_000),
  importSound: (src) => keepCopy(DIRS.sounds, src, SOUND_EXT, MAX_SOUND),
  catalog,
  itemSummary: (item) => watch.summary(item),
  itemIsBuild: (item) => watch.inBuild(item),
  // Promise of a data URL for the pairing QR code (qrcode package)
  qr: (text) => require('qrcode').toDataURL(String(text), { margin: 1, width: 240, color: { dark: '#000000', light: '#FFFFFF' } }),
});
