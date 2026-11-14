'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const S = require('../src/core/settings');

function tmp() { return fs.mkdtempSync(path.join(os.tmpdir(), 'nt-settings-')); }

test('defaults match Settings.swift and skip the Mac-only modules', () => {
  const dir = tmp();
  const s = S.load(dir);
  assert.strictEqual(s.expandMode, 'hover');
  assert.strictEqual(s.hoverDelay, 0.15);
  assert.strictEqual(s.size, 'compact');
  assert.strictEqual(s.accent, 'auto');
  assert.strictEqual(s.background, 'black');
  assert.strictEqual(s.toastStyle, 'island');
  assert.strictEqual(s.motion, 'bouncy');
  assert.strictEqual(s.doneAfter, 30);
  assert.strictEqual(s.limitWarn, 80);
  assert.strictEqual(s.volume, 0.8);
  assert.strictEqual(s.onboardingDone, false);
  assert.deepStrictEqual(s.disabled, ['timer']);
  assert.deepStrictEqual(s.soundMap, S.SOUND_PACKS[0].map);
  assert.deepStrictEqual(s.badges, []);
  assert.deepStrictEqual(s.modules, S.modulesAvailable());
  for (const gone of ['haptics', 'menuBarIcon', 'lockScreen', 'lockShows', 'lockAuth']) assert.ok(!(gone in s), gone);
});

test('modulesAvailable leaves out the Windows-missing modules, in Mac order', () => {
  assert.deepStrictEqual(S.modulesAvailable(), ['agents', 'ci', 'media', 'timer', 'shelf', 'limits', 'battery', 'network',
    'downloads', 'health']);
  for (const m of S.MISSING_ON_WINDOWS) assert.ok(!S.modulesAvailable().includes(m));
});

test('set validates enums, ranges, colors and types', () => {
  const dir = tmp();
  S.load(dir);
  assert.throws(() => S.set('size', 'huge'), TypeError);
  assert.throws(() => S.set('hoverDelay', 1.5), TypeError);
  assert.throws(() => S.set('doneAfter', 2), TypeError);
  assert.throws(() => S.set('limitWarn', 49), TypeError);
  assert.throws(() => S.set('customAccent', 'red'), TypeError);
  assert.throws(() => S.set('sounds', 'yes'), TypeError);
  assert.throws(() => S.set('soundMap', { taskDone: 'NoSuchSound' }), TypeError);
  assert.throws(() => S.set('moduleColors', { calendar: '#zzzzzz' }), TypeError);
  assert.throws(() => S.set('nope', 1), TypeError);
  assert.throws(() => S.set('modules', ['agents', 'nope']), TypeError);
  assert.strictEqual(S.get().size, 'compact', 'a rejected value changes nothing');
});

test('set returns the new settings and tells listeners', () => {
  const dir = tmp();
  S.load(dir);
  const seen = [];
  S.on((settings, keys) => seen.push([settings.volume, keys]));
  const next = S.set('volume', 0.5);
  assert.strictEqual(next.volume, 0.5);
  assert.deepStrictEqual(seen, [[0.5, ['volume']]]);
});

test('modules drops missing ones and appends the rest that were not saved', () => {
  const dir = tmp();
  S.load(dir);
  const next = S.set('modules', ['health', 'calendar', 'agents']);
  assert.deepStrictEqual(next.modules.slice(0, 2), ['health', 'agents']);
  assert.ok(!next.modules.includes('calendar'));
  assert.strictEqual(next.modules.length, S.modulesAvailable().length);
});

test('badges are checked field by field and capped per side', () => {
  const dir = tmp();
  S.load(dir);
  const b = S.set('badges', [{ symbol: 'bolt', right: true, shape: 'rounded', text: '🔥' }]).badges[0];
  assert.strictEqual(b.symbol, 'bolt');
  assert.strictEqual(b.shape, 'rounded');
  assert.strictEqual(b.right, true);
  assert.ok(b.id && b.id.length > 0, 'an id is made when none is given');
  assert.throws(() => S.set('badges', [{ text: 'toolong text' }]), TypeError);
  assert.throws(() => S.set('badges', [{ image: 'relative.png' }]), TypeError);
  const five = Array.from({ length: 5 }, () => ({ right: false }));
  assert.throws(() => S.set('badges', five), TypeError);
});

test('applyTheme sets the look and keeps sounds and modules', () => {
  const dir = tmp();
  S.load(dir);
  const sounds = S.set('soundMap', { taskDone: 'Hero' }).soundMap;
  const next = S.applyTheme('neon');
  assert.strictEqual(next.background, 'glow');
  assert.strictEqual(next.customAccent, '#00F5D4');
  assert.strictEqual(next.soundMap.taskDone, sounds.taskDone);
  assert.throws(() => S.applyTheme('nope'), TypeError);
});

test('profile export and import round trip; a bad profile is refused whole', () => {
  const dir = tmp();
  S.load(dir);
  S.set('size', 'regular');
  const exported = S.exportProfile();
  assert.ok(!('haptics' in exported));
  S.set('size', 'compact');
  S.importProfile(exported);
  assert.strictEqual(S.get().size, 'regular');
  assert.throws(() => S.importProfile({ size: 'huge', volume: 0.1 }), TypeError);
  assert.strictEqual(S.get().volume, exported.volume, 'nothing from the bad profile was applied');
});

test('values survive a round trip through the file', () => {
  const dir = tmp();
  S.load(dir);
  S.set('expandMode', 'click');
  S.set('moduleColors', { agents: '#ABCDEF' });
  S.set('seenTools', ['Docker', 'npm']);
  const again = S.load(dir);
  assert.strictEqual(again.expandMode, 'click');
  assert.deepStrictEqual(again.moduleColors, { agents: '#ABCDEF' });
  assert.deepStrictEqual(again.seenTools, ['Docker', 'npm']);
  assert.ok(!fs.existsSync(path.join(dir, 'settings.json.tmp')), 'no temp file left behind');
});

test('a corrupt file loads as defaults, and a bad field alone falls back', () => {
  const dir = tmp();
  fs.writeFileSync(path.join(dir, 'settings.json'), '{not json');
  assert.deepStrictEqual(S.load(dir), { ...S.defaults(), modules: S.modulesAvailable() });

  fs.writeFileSync(path.join(dir, 'settings.json'), JSON.stringify({ size: 'huge', volume: 0.3, badges: 'x' }));
  const s = S.load(dir);
  assert.strictEqual(s.size, 'compact');
  assert.strictEqual(s.volume, 0.3);
  assert.deepStrictEqual(s.badges, []);
});

test('a missing folder is created on the first save', () => {
  const dir = path.join(tmp(), 'sub', 'NotchTasks');
  S.load(dir);
  S.set('volume', 0.2);
  assert.strictEqual(JSON.parse(fs.readFileSync(path.join(dir, 'settings.json'), 'utf8')).volume, 0.2);
});

test('watch items: kinds and paths are checked, an id is made when missing', () => {
  const abs = process.platform === 'win32' ? 'C:\\Apps\\Cursor\\Cursor.exe' : '/Apps/Cursor/Cursor.exe';
  const [it] = S.SPEC.items.check([{ name: 'Cursor', kind: 'app', exe: abs, bundleID: 'Cursor.exe' }]);
  assert.strictEqual(it.kind, 'app');
  assert.strictEqual(it.enabled, true);
  assert.match(it.id, /^[\w-]{1,64}$/);
  assert.throws(() => S.SPEC.items.check([{ name: 'x', kind: 'shell' }]));
  assert.throws(() => S.SPEC.items.check([{ name: 'x', kind: 'app', exe: 'Cursor.exe' }]));
});

test('language: a code or empty; badge symbols stay SF Symbol names', () => {
  assert.strictEqual(S.SPEC.language.check('pt-BR'), 'pt-BR');
  assert.strictEqual(S.SPEC.language.check(''), '');
  assert.throws(() => S.SPEC.language.check('../etc'));
  assert.strictEqual(S.badge({ symbol: 'bolt.fill' }).symbol, 'bolt.fill');
});
