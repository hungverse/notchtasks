'use strict';
// Port of Sources/Settings.swift (AppSettings: property names = the keys stored) and the value types in
// Sources/Customize.swift. Pure Node, no electron: main calls load(%APPDATA%\NotchTasks) once.
// Left out, because they only exist on a Mac: haptics (trackpad), menuBarIcon (the tray icon is always there),
// lockScreen, lockShows, lockOwnBadges, lockBadges, lockIconRight, lockBatteryLeft, lockAuth (lock screen notch),
// and the fan settings of FanService/Fans.swift.
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

// Mac modules Windows cannot show: calendar (EventKit), devices (AirPods batteries), calls (FaceTime/Zoom mic),
// privacy (mic/camera use), levels (the system volume/brightness HUD), mirror (camera preview).
const MISSING_ON_WINDOWS = ['calendar', 'devices', 'calls', 'privacy', 'levels', 'mirror'];
// ModuleID.allCases, in the Mac's order (the default order on the notch).
const MODULE_IDS = ['agents', 'ci', 'media', 'calendar', 'timer', 'shelf', 'limits', 'battery', 'devices', 'calls', 'privacy',
  'levels', 'network', 'downloads', 'health', 'mirror'];

// Title (a Vietnamese key for nt.L), blurb and icon (lucide name) per module that exists on Windows.
const MODULE_INFO = {
  agents: { title: 'AI agent', blurb: 'Agent và app AI đang làm việc', icon: 'sparkles' },
  ci: { title: 'Build & CI', blurb: 'Build, test, cài đặt, render, CI', icon: 'hammer' },
  media: { title: 'Đang phát', blurb: 'Bài đang phát, có nút điều khiển', icon: 'music' },
  timer: { title: 'Hẹn giờ', blurb: 'Hẹn giờ nhanh', icon: 'timer' },
  shelf: { title: 'Khay file', blurb: 'Kéo file lên notch để giữ tạm', icon: 'inbox' },
  limits: { title: 'Giới hạn AI', blurb: 'Mức dùng agent, báo khi sắp hết', icon: 'gauge' },
  battery: { title: 'Pin', blurb: 'Cắm, rút sạc, pin yếu', icon: 'battery-medium' },
  network: { title: 'Mạng', blurb: 'Mất mạng, có mạng lại, VPN', icon: 'wifi' },
  downloads: { title: 'Tải xuống', blurb: 'Báo tải xong, mở nơi lưu', icon: 'download' },
  health: { title: 'Sức khoẻ máy', blurb: 'Máy nóng, ổ đĩa sắp đầy', icon: 'thermometer' },
};
// Where it sits on the Overview page (ModuleID.groups without the Mac-only modules).
const MODULE_GROUPS = [
  { title: 'Công việc', ids: ['agents', 'ci', 'limits', 'timer'] },
  { title: 'Nhạc & thiết bị', ids: ['media', 'battery'] },
  { title: 'Hệ thống', ids: ['network', 'downloads', 'health', 'shelf'] },
];

const EXPAND_MODES = ['hover', 'click'];
const SIZES = ['compact', 'regular'];
const BACKGROUNDS = ['black', 'glass', 'gradient', 'glow'];
const TOAST_STYLES = ['island', 'compact', 'card', 'banner'];
const MOTIONS = ['smooth', 'bouncy', 'snappy', 'reduced'];
const SHAPES = ['circle', 'rounded'];
const PLACEMENTS = ['auto', 'left', 'right'];
const PER_SIDE = 2;      // icons a side of the notch; Pro gets 4 (the validation allows 4)
const PRO_PER_SIDE = 4;

// Accent palette, Apple's system colors as hex (the Mac used the system named colors).
const ACCENTS = [
  { name: 'auto', title: 'Theo màu app', hex: null },
  { name: 'blue', title: 'Xanh dương', hex: '#0A84FF' },
  { name: 'green', title: 'Xanh lá', hex: '#30D158' },
  { name: 'orange', title: 'Cam', hex: '#FF9F0A' },
  { name: 'pink', title: 'Hồng', hex: '#FF375F' },
  { name: 'purple', title: 'Tím', hex: '#BF5AF2' },
  { name: 'white', title: 'Trắng', hex: '#FFFFFF' },
  { name: 'custom', title: 'Tự chọn…', hex: null },
];

// Sound names as the Mac pack tables use them. The Windows player maps each to a bundled file.
const SOUND_NAMES = ['Basso', 'Blow', 'Bottle', 'Frog', 'Funk', 'Glass', 'Hero', 'Morse', 'Ping', 'Pop', 'Purr', 'Sosumi',
  'Submarine', 'Tink'];

const SOUND_EVENTS = [
  { id: 'taskDone', title: 'AI / tác vụ xong', icon: 'circle-check' },
  { id: 'agentAsk', title: 'Agent hỏi, cần duyệt', icon: 'hand' },
  { id: 'ciPassed', title: 'CI đạt', icon: 'badge-check' },
  { id: 'ciFailed', title: 'CI lỗi', icon: 'octagon-x' },
  { id: 'limitWarn', title: 'Sắp hết giới hạn AI', icon: 'gauge' },
  { id: 'meeting', title: 'Sự kiện sắp tới', icon: 'calendar' },
  { id: 'timerDone', title: 'Hết giờ hẹn', icon: 'timer' },
  { id: 'charging', title: 'Cắm sạc', icon: 'zap' },
  { id: 'unplugged', title: 'Rút sạc', icon: 'plug-zap' },
  { id: 'lowBattery', title: 'Pin yếu', icon: 'battery-low' },
  { id: 'deviceOn', title: 'Thiết bị kết nối', icon: 'headphones' },
  { id: 'deviceOff', title: 'Thiết bị ngắt', icon: 'headphones' },
  { id: 'callStart', title: 'Bắt đầu cuộc gọi', icon: 'phone' },
  { id: 'callEnd', title: 'Kết thúc cuộc gọi', icon: 'phone-off' },
  { id: 'sensorOn', title: 'Mic hoặc camera bật', icon: 'mic' },
  { id: 'authFail', title: 'Mở khoá sai', icon: 'shield-alert' },
  { id: 'netDown', title: 'Mất mạng', icon: 'wifi-off' },
  { id: 'netUp', title: 'Có mạng lại, VPN', icon: 'wifi' },
  { id: 'downloadDone', title: 'Tải xong', icon: 'download' },
  { id: 'warning', title: 'Máy nóng, ổ đĩa đầy', icon: 'triangle-alert' },
  { id: 'open', title: 'Mở notch', icon: 'chevron-down' },
  { id: 'close', title: 'Thu gọn notch', icon: 'chevron-up' },
];

// SoundPack.all: the built-in packs. '' = silent.
function soundMapOf(names) { return Object.fromEntries(SOUND_EVENTS.map((e) => [e.id, names[e.id] ?? ''])); }
const SOUND_PACKS = [
  { name: 'Mặc định', map: soundMapOf({ taskDone: 'Glass', agentAsk: 'Ping', ciPassed: 'Hero', ciFailed: 'Basso', limitWarn: 'Funk', meeting: 'Ping',
    timerDone: 'Glass', charging: 'Pop', unplugged: '', lowBattery: 'Funk', deviceOn: 'Pop', deviceOff: 'Bottle', callStart: 'Tink', callEnd: 'Bottle',
    sensorOn: '', authFail: 'Basso', netDown: 'Basso', netUp: 'Tink', downloadDone: 'Glass', warning: 'Funk', open: '', close: '' }) },
  { name: 'Tối giản', map: soundMapOf({ taskDone: 'Tink', agentAsk: 'Tink', ciPassed: 'Tink', ciFailed: 'Pop', limitWarn: 'Pop', meeting: 'Tink',
    timerDone: 'Tink', charging: 'Tink', unplugged: '', lowBattery: 'Pop', deviceOn: 'Tink', deviceOff: '', callStart: 'Tink', callEnd: '',
    sensorOn: '', authFail: 'Pop', netDown: 'Pop', netUp: '', downloadDone: 'Tink', warning: 'Pop', open: '', close: '' }) },
  { name: 'Sôi động', map: soundMapOf({ taskDone: 'Hero', agentAsk: 'Submarine', ciPassed: 'Hero', ciFailed: 'Sosumi', limitWarn: 'Funk', meeting: 'Glass',
    timerDone: 'Submarine', charging: 'Bottle', unplugged: 'Blow', lowBattery: 'Funk', deviceOn: 'Hero', deviceOff: 'Blow', callStart: 'Glass',
    callEnd: 'Blow', sensorOn: 'Pop', authFail: 'Sosumi', netDown: 'Sosumi', netUp: 'Hero', downloadDone: 'Hero', warning: 'Funk', open: 'Pop', close: 'Tink' }) },
  { name: 'Retro', map: soundMapOf({ taskDone: 'Morse', agentAsk: 'Ping', ciPassed: 'Frog', ciFailed: 'Basso', limitWarn: 'Purr', meeting: 'Ping',
    timerDone: 'Submarine', charging: 'Bottle', unplugged: 'Blow', lowBattery: 'Sosumi', deviceOn: 'Morse', deviceOff: 'Purr', callStart: 'Morse',
    callEnd: 'Purr', sensorOn: '', authFail: 'Basso', netDown: 'Basso', netUp: 'Morse', downloadDone: 'Frog', warning: 'Sosumi', open: '', close: '' }) },
  { name: 'Im lặng', map: soundMapOf({}) },
];

// Themes (Customize.swift Theme.all): one click sets the look, never the sounds.
const THEMES = [
  { id: 'classic', title: 'Cổ điển', background: 'black', gradientA: '#1E1B4B', gradientB: '#000000', accent: 'auto', customAccent: '#FF9F0A', toastStyle: 'island', motion: 'bouncy' },
  { id: 'neon', title: 'Neon', background: 'glow', gradientA: '#0F172A', gradientB: '#000000', accent: 'custom', customAccent: '#00F5D4', toastStyle: 'card', motion: 'bouncy' },
  { id: 'glass', title: 'Kính', background: 'glass', gradientA: '#334155', gradientB: '#0F172A', accent: 'white', customAccent: '#FFFFFF', toastStyle: 'banner', motion: 'smooth' },
  { id: 'minimal', title: 'Tối giản', background: 'black', gradientA: '#111111', gradientB: '#000000', accent: 'white', customAccent: '#FFFFFF', toastStyle: 'compact', motion: 'snappy' },
  { id: 'sunset', title: 'Hoàng hôn', background: 'gradient', gradientA: '#FF5F6D', gradientB: '#5B247A', accent: 'custom', customAccent: '#FFC371', toastStyle: 'card', motion: 'bouncy' },
  { id: 'ocean', title: 'Đại dương', background: 'gradient', gradientA: '#0EA5E9', gradientB: '#1E1B4B', accent: 'custom', customAccent: '#7DD3FC', toastStyle: 'island', motion: 'smooth' },
];

// Settings.profile: what Export/Import in Appearance carries (haptics and the lock screen are Mac-only).
const PROFILE_KEYS = ['modules', 'disabled', 'expandMode', 'hoverDelay', 'sounds', 'systemNotifications', 'size', 'accent', 'customAccent',
  'doneAfter', 'limitWarn', 'background', 'gradientA', 'gradientB', 'toastStyle', 'motion', 'moduleColors', 'placements', 'soundMap', 'volume'];

// ---- validation -------------------------------------------------------------------------------------------------

function fail(key, want) { throw new TypeError(`${key}: expected ${want}`); }
const bool = (key) => (v) => { if (typeof v !== 'boolean') fail(key, 'true or false'); return v; };
const num = (key, min, max) => (v) => {
  if (typeof v !== 'number' || !Number.isFinite(v) || v < min || v > max) fail(key, `a number from ${min} to ${max}`);
  return v;
};
const oneOf = (key, list) => (v) => { if (!list.includes(v)) fail(key, list.join(' | ')); return v; };
const hex = (key) => (v) => {
  if (typeof v !== 'string' || !/^#[0-9a-fA-F]{6}$/.test(v)) fail(key, 'a #RRGGBB color');
  return v.toUpperCase();
};
// `max` counts characters (code points), so an emoji is one.
const text = (key, max) => (v) => {
  if (typeof v !== 'string' || Array.from(v).length > max) fail(key, `text up to ${max} characters`);
  return v;
};
const unique = (arr) => [...new Set(arr)];
const list = (key, item, max) => (v) => {
  if (!Array.isArray(v) || v.length > max) fail(key, `a list of up to ${max} items`);
  return v.map(item);
};
const dict = (key, valueCheck, keyCheck) => (v) => {
  if (!v || typeof v !== 'object' || Array.isArray(v)) fail(key, 'an object');
  const out = {};
  for (const [k, val] of Object.entries(v)) out[keyCheck(k)] = valueCheck(val);
  return out;
};
const moduleId = oneOf('module', MODULE_IDS);
const soundEventId = oneOf('sound event', SOUND_EVENTS.map((e) => e.id));
const soundValue = (v) => {
  if (v === '' || SOUND_NAMES.includes(v)) return v;
  if (typeof v === 'string' && v.length <= 500 && path.isAbsolute(v)) return v;  // the user's own file
  return fail('sound', 'a sound name, "" or an absolute file path');
};

function badge(v) {
  if (!v || typeof v !== 'object' || Array.isArray(v)) fail('badge', 'an object');
  const opt = (x, check) => (x === undefined || x === null ? null : check(x));
  return {
    id: typeof v.id === 'string' && /^[\w-]{1,64}$/.test(v.id) ? v.id : crypto.randomUUID(),
    symbol: text('symbol', 80)(v.symbol ?? 'star'),           // an SF Symbol name that icons.js maps (the phone uses the same)
    image: opt(v.image, (x) => {
      if (typeof x !== 'string' || x.length > 500 || !path.isAbsolute(x)) fail('image', 'an absolute file path');
      return x;
    }),
    text: opt(v.text, text('text', 8)),
    color: opt(v.color, hex('color')),
    fg: opt(v.fg, hex('fg')),
    plain: opt(v.plain, bool('plain')),
    shape: oneOf('shape', SHAPES)(v.shape ?? 'circle'),
    border: bool('border')(v.border ?? false),
    right: bool('right')(v.right ?? false),
    sprite: opt(v.sprite, text('sprite', 80)),
    frames: opt(v.frames, (x) => Math.round(num('frames', 1, 120)(x))),
    fps: opt(v.fps, num('fps', 1, 30)),
  };
}

function badgeList(v) {
  const out = list('badges', badge, PRO_PER_SIDE * 2)(v);
  for (const right of [false, true]) {
    if (out.filter((b) => b.right === right).length > PRO_PER_SIDE) fail('badges', `at most ${PRO_PER_SIDE} per side`);
  }
  return out;
}

// Modules that are missing on Windows are never in the list; the rest are added at the end if absent.
function withAvailable(ids) {
  const out = unique(ids).filter((id) => !MISSING_ON_WINDOWS.includes(id));
  return [...out, ...modulesAvailable().filter((id) => !out.includes(id))];
}

// Watch list (Sources/WatchItems.swift WatchItem). The process list itself is read by main; this only keeps the items valid.
const ITEM_KINDS = ['process', 'app', 'codex', 'claude', 'dev'];
function watchItem(v) {
  if (!v || typeof v !== 'object' || Array.isArray(v)) fail('item', 'an object');
  const opt = (x, check) => (x === undefined || x === null ? null : check(x));
  return {
    id: typeof v.id === 'string' && /^[\w-]{1,64}$/.test(v.id) ? v.id : crypto.randomUUID(),
    name: text('name', 80)(v.name ?? ''),
    kind: oneOf('kind', ITEM_KINDS)(v.kind),
    process: text('process', 200)(v.process ?? ''),
    args: opt(v.args, text('args', 200)),
    childTasks: bool('childTasks')(v.childTasks ?? false),
    enabled: bool('enabled')(v.enabled ?? true),
    threshold: opt(v.threshold, num('threshold', 0.5, 100)),
    bundleID: opt(v.bundleID, text('bundleID', 200)),
    exe: opt(v.exe, (x) => {
      if (typeof x !== 'string' || x.length > 500 || !path.isAbsolute(x)) fail('exe', 'an absolute path');
      return x;
    }),
    cli: opt(v.cli, text('cli', 80)),
    build: bool('build')(v.build ?? false),
    symbolName: opt(v.symbolName, text('symbolName', 80)),
    icon: opt(v.icon, badge),
  };
}
const watchItems = list('items', watchItem, 64);
// The built-in list on a first run: Claude and Codex (main adds the agents found on this PC once).
const defaultItems = () => require('./watchitems').presets.slice(0, 2).map((p) => ({ ...p }));

// The app's language as the user picked it in Behavior: '' follows the device, else a code from l10n.languages.
const language = (v) => {
  if (typeof v !== 'string' || !/^([a-zA-Z]{2,3}(-[A-Za-z]{2,4})?)?$/.test(v)) fail('language', 'a language code or ""');
  return v;
};

const SPEC = {
  items: { def: defaultItems, check: watchItems },
  language: { def: () => '', check: language },
  modules: { def: () => modulesAvailable(), check: (v) => withAvailable(list('modules', moduleId, MODULE_IDS.length)(v)) },
  disabled: { def: () => ['timer'], check: (v) => unique(list('disabled', moduleId, MODULE_IDS.length)(v)) },
  expandMode: { def: () => 'hover', check: oneOf('expandMode', EXPAND_MODES) },
  hoverDelay: { def: () => 0.15, check: num('hoverDelay', 0, 1) },
  systemLoad: { def: () => true, check: bool('systemLoad') },
  sounds: { def: () => true, check: bool('sounds') },
  systemNotifications: { def: () => true, check: bool('systemNotifications') },
  size: { def: () => 'compact', check: oneOf('size', SIZES) },
  accent: { def: () => 'auto', check: oneOf('accent', ACCENTS.map((a) => a.name)) },
  customAccent: { def: () => '#FF9F0A', check: hex('customAccent') },
  onboardingDone: { def: () => false, check: bool('onboardingDone') },
  doneAfter: { def: () => 30, check: num('doneAfter', 5, 600) },  // a task must run this long (seconds) to be announced
  limitWarn: { def: () => 80, check: num('limitWarn', 50, 100) },  // % used that triggers a warning
  background: { def: () => 'black', check: oneOf('background', BACKGROUNDS) },
  gradientA: { def: () => '#1E1B4B', check: hex('gradientA') },
  gradientB: { def: () => '#000000', check: hex('gradientB') },
  toastStyle: { def: () => 'island', check: oneOf('toastStyle', TOAST_STYLES) },
  motion: { def: () => 'bouncy', check: oneOf('motion', MOTIONS) },
  moduleColors: { def: () => ({}), check: dict('moduleColors', hex('moduleColor'), moduleId) },
  placements: { def: () => ({}), check: dict('placements', oneOf('placement', PLACEMENTS), moduleId) },
  soundMap: { def: () => SOUND_PACKS[0].map, check: dict('soundMap', soundValue, soundEventId) },
  volume: { def: () => 0.8, check: num('volume', 0, 1) },
  badges: { def: () => [], check: badgeList },
  toolIcons: { def: () => ({}), check: dict('toolIcons', badge, (k) => text('tool', 60)(k)) },
  seenTools: { def: () => [], check: (v) => unique(list('seenTools', (x) => text('tool', 60)(x), 200)(v)) },
};
const KEYS = Object.keys(SPEC);

function modulesAvailable() { return MODULE_IDS.filter((id) => !MISSING_ON_WINDOWS.includes(id)); }

function defaults() { return Object.fromEntries(KEYS.map((k) => [k, SPEC[k].def()])); }

// ---- state, persistence -----------------------------------------------------------------------------------------

let dir = null;
let current = defaults();
const listeners = [];

function file() { return path.join(dir || '.', 'settings.json'); }

// Writes a temporary file and renames it over the old one, so a crash never leaves half a file.
function save(next) {
  if (!dir) throw new Error('settings: load(dir) first');
  fs.mkdirSync(dir, { recursive: true });
  const tmp = `${file()}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify(next, null, 2), 'utf8');
  fs.renameSync(tmp, file());
}

// Each key checked on its own: one bad value takes its default, the rest of the file still loads.
function sanitize(data) {
  const out = defaults();
  if (!data || typeof data !== 'object' || Array.isArray(data)) return out;
  for (const k of KEYS) {
    if (data[k] === undefined) continue;
    try { out[k] = SPEC[k].check(data[k]); } catch { /* keep the default */ }
  }
  return out;
}

/** Reads `<dir>/settings.json` (created on the first set). A missing or corrupt file gives the defaults. */
function load(folder) {
  dir = folder;
  let data = null;
  try { data = JSON.parse(fs.readFileSync(file(), 'utf8')); } catch { data = null; }
  current = sanitize(data);
  return get();
}

function get() { return structuredClone(current); }

function apply(changes) {
  const next = { ...current };
  for (const [k, v] of Object.entries(changes)) {
    if (!(k in SPEC)) throw new TypeError(`unknown setting: ${k}`);
    next[k] = SPEC[k].check(v);
  }
  save(next);  // throws before anything changes in memory
  current = next;
  const snapshot = get();
  for (const fn of listeners) fn(snapshot, Object.keys(changes));
  return snapshot;
}

/** Sets one key (validated, saved atomically). Returns the new settings and tells the listeners. */
function set(key, value) { return apply({ [key]: value }); }

/** Several keys at once, one save. */
function setMany(changes) { return apply(changes); }

/** Calls fn(settings, changedKeys) after every change. */
function on(fn) { listeners.push(fn); }

/** Sets the look of a built-in theme; sounds, modules and badges stay as they are. */
function applyTheme(id) {
  const t = THEMES.find((x) => x.id === id);
  if (!t) throw new TypeError(`unknown theme: ${id}`);
  return apply({ background: t.background, gradientA: t.gradientA, gradientB: t.gradientB, accent: t.accent,
    customAccent: t.customAccent, toastStyle: t.toastStyle, motion: t.motion });
}

/** The shareable part (Settings.profile), as a plain object for an export file. */
function exportProfile() {
  const s = get();
  return Object.fromEntries(PROFILE_KEYS.map((k) => [k, s[k]]));
}

/** Applies an imported profile. A bad value rejects the whole file; unknown keys are ignored. */
function importProfile(obj) {
  if (!obj || typeof obj !== 'object' || Array.isArray(obj)) throw new TypeError('profile: expected an object');
  const changes = {};
  for (const k of PROFILE_KEYS) if (obj[k] !== undefined) changes[k] = obj[k];
  return apply(changes);
}

module.exports = {
  load, get, set, setMany, on, applyTheme, exportProfile, importProfile, modulesAvailable,
  defaults, sanitize, badge, SPEC,
  MISSING_ON_WINDOWS, MODULE_IDS, MODULE_INFO, MODULE_GROUPS, EXPAND_MODES, SIZES, BACKGROUNDS, TOAST_STYLES, MOTIONS, SHAPES,
  PLACEMENTS, PER_SIDE, PRO_PER_SIDE, ACCENTS, SOUND_NAMES, SOUND_EVENTS, SOUND_PACKS, THEMES, PROFILE_KEYS,
};
