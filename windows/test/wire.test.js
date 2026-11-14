'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const box = require('../src/core/box');
const wire = require('../src/core/wire');
const { toSwiftDate, fromSwiftDate } = require('../src/core/swiftjson');

const FIX = path.join(__dirname, 'fixtures', 'crosscheck');
const OUT = path.join(__dirname, '..', 'build', 'crosscheck'); // read by tools/crosscheck.sh's `check` step
const fixture = (name) => fs.readFileSync(path.join(FIX, name), 'utf8').trim();
const KEY = Buffer.from(fixture('key.txt'), 'base64url');
const openFix = (name) => box.open(fixture(name), KEY);
const T0 = new Date(1794380000250); // the Swift fixtures' time: 1794380000.25 since 1970
const T0_SWIFT = 816072800.25;      // the same moment as Swift's reference date (since 2001)
const UUID_A = '6F9619FF-8B86-D011-B42D-00C04FC964FF';
const UUID_B = '904DCCFE-D16E-4571-AB4E-BEFC72ADF9C8';
const UUID_C = '0B5B8C2E-3D4F-4A67-9C10-8E1D2F3A4B5C';

test('Swift dates: seconds since 2001-01-01, ms in and out', () => {
  assert.equal(toSwiftDate(new Date(Date.UTC(2001, 0, 1))), 0);
  assert.equal(toSwiftDate(new Date(978307200000 + 1250)), 1.25);
  assert.equal(toSwiftDate(T0), T0_SWIFT);
  assert.equal(toSwiftDate(1794380000250), T0_SWIFT, 'ms numbers too');
  assert.equal(fromSwiftDate(0).toISOString(), '2001-01-01T00:00:00.000Z');
  assert.equal(fromSwiftDate(T0_SWIFT).getTime(), T0.getTime());
  assert.equal(fromSwiftDate(openFix('snapshot.box').at).getTime(), T0.getTime(), 'the Swift snapshot date');
  assert.throws(() => fromSwiftDate('1'));
});

test('the Swift-sealed answers open and mean what they say (iOS Answer, sealed by Swift)', () => {
  const cases = [
    ['swift-allow.box', { ask: 'a1', reply: { kind: 'allow' } }],
    ['swift-deny.box', { ask: 'a1', reply: { kind: 'deny' } }],
    ['swift-pass.box', { ask: 'a1', reply: { kind: 'pass' } }],
    ['swift-answers.box', { ask: 'a2', reply: { kind: 'answers', answers: { 'Which DB?': 'SQLite, JSON' } } }],
  ];
  for (const [name, want] of cases) {
    const a = wire.readAnswer(openFix(name));
    assert.deepEqual({ ask: a.ask, reply: a.reply }, want, name);
    assert.equal(a.at.getTime(), T0.getTime(), `${name} date`);
    assert.equal('phone' in a, false, `${name} has no phone`);
  }
  const hello = wire.readAnswer(openFix('swift-hello.box'));
  assert.equal(hello.ask, '');
  assert.equal(hello.phone, 'uid-1');
  assert.deepEqual(hello.reply, { kind: 'pass' });
  const timer = wire.readAnswer(openFix('swift-timer.box'));
  assert.equal(timer.timer, 25);
  assert.equal('notch' in timer, false);
  assert.equal('refresh' in timer, false);
  assert.equal(wire.readAnswer(openFix('swift-refresh.box')).refresh, true);
  const n = wire.readAnswer(openFix('swift-notch.box')).notch;
  assert.equal(n.badges.length, 3);
  assert.equal(n.badges[0].sprite, 'px-buddy');
  assert.equal(n.badges[2].text, 'AI');
  assert.equal(n.badges[2].border, true);
  assert.equal(n.badges[2].right, true);
  assert.equal(n.badges[2].shape, 'rounded');
  assert.equal(n.badges[2].id, UUID_A, 'the id Swift wrote');
  assert.equal(n.toolIcons.Xcode.sprite, 'px-gears');
  assert.equal(n.accent, 'custom');
  assert.equal(n.customAccent, '#00F5D4');
  assert.equal(n.modules.length, 16);
});

test('readAnswer refuses what a phone could not have sent', () => {
  const good = { ask: 'a1', reply: { allow: {} }, at: T0_SWIFT };
  assert.equal(wire.readAnswer(good).reply.kind, 'allow');
  assert.throws(() => wire.readAnswer({ ...good, ask: undefined }), 'no ask');
  assert.throws(() => wire.readAnswer({ ...good, reply: { boom: {} } }), 'unknown reply');
  assert.throws(() => wire.readAnswer({ ...good, reply: { answers: { _0: { q: 1 } } } }), 'answer not a string');
  assert.throws(() => wire.readAnswer({ ...good, at: 'x' }), 'bad date');
  assert.throws(() => wire.readAnswer({ ...good, timer: 1.5 }), 'timer not an integer');
  assert.throws(() => wire.readAnswer({ ...good, refresh: 'yes' }), 'refresh not a boolean');
  assert.throws(() => wire.readAnswer(null), 'not an object');
});

test('encodeReply writes Swift enum shapes; decodeReply reads them back', () => {
  assert.equal(JSON.stringify(wire.encodeReply({ kind: 'allow' })), '{"allow":{}}');
  assert.equal(JSON.stringify(wire.encodeReply({ kind: 'deny' })), '{"deny":{}}');
  assert.equal(JSON.stringify(wire.encodeReply({ kind: 'pass' })), '{"pass":{}}');
  assert.equal(JSON.stringify(wire.encodeReply({ kind: 'answers', answers: { q: 'a' } })), '{"answers":{"_0":{"q":"a"}}}');
  assert.equal(JSON.stringify(wire.encodeReply({ kind: 'answers', answers: {} })), '{"answers":{"_0":{}}}');
  for (const r of [{ kind: 'allow' }, { kind: 'deny' }, { kind: 'pass' }, { kind: 'answers', answers: { 'Which DB?': 'SQLite, JSON' } }]) {
    assert.deepEqual(wire.decodeReply(JSON.parse(JSON.stringify(wire.encodeReply(r)))), r);
  }
  assert.deepEqual(wire.decodeReply({ answers: { _0: { 'Which DB?': 'SQLite, JSON' } } }), { kind: 'answers', answers: { 'Which DB?': 'SQLite, JSON' } });
  for (const bad of [{}, { allow: null }, { answers: {} }, { allow: {}, deny: {} }, ['allow'], 'allow']) {
    assert.throws(() => wire.decodeReply(bad), undefined, JSON.stringify(bad));
  }
  assert.throws(() => wire.encodeReply({ kind: 'maybe' }));
});

test('the Swift reference snapshot: its fields, and nil keys left out as Swift leaves them', () => {
  const s = openFix('snapshot.box');
  assert.equal(s.mac, 'Hùng’s MacBook "Pro"');
  assert.equal(s.asks[0].icon.sprite, 'px-cat');
  assert.equal(s.asks[0].questions.length, 0);
  assert.equal(s.asks[1].agent, 'codex');
  assert.equal(s.asks[1].questions[0].header, 'DB');
  assert.equal('icon' in s.asks[1], false, 'a nil icon is no key');
  assert.equal('agent' in s.builds[0], false, 'a nil agent is no key');
  assert.equal('ended' in s.job, false);
  assert.equal(s.health.battery, 64);
  assert.equal(s.notch.badges.length, 3);
  assert.equal(s.crew.members.length, 1);
  assert.equal(s.limits[0].resets, T0_SWIFT + 3600);
});

// The full snapshot the Mac would send: every field, with an icon, a notch, a job, a crew.
function fullNotch() {
  return {
    size: 'compact', expandMode: 'hover', accent: 'custom', customAccent: '#00F5D4', background: 'glow', gradientA: '#0F172A',
    gradientB: '#000000', toastStyle: 'card', motion: 'bouncy',
    modules: ['agents', 'ci', 'media', 'calendar', 'timer', 'shelf', 'limits', 'battery', 'devices', 'calls', 'privacy', 'levels', 'network', 'downloads', 'health', 'mirror'],
    disabled: ['mirror', 'shelf'], placements: { agents: 'wings' }, haptics: true, sounds: true, volume: 0.8, systemNotifications: true,
    doneAfter: 30, limitWarn: 80, lockScreen: true, lockShows: ['lock'],
    badges: [
      { id: UUID_A, symbol: 'star.fill', sprite: 'px-buddy' },
      { id: UUID_B, symbol: 'star.fill', sprite: 'px-cat', right: true },
      { id: UUID_C, symbol: 'bolt.fill', text: 'AI', color: '#FF375F', fg: '#FFFFFF', plain: false, shape: 'rounded', border: true, right: true, frames: 4, fps: 12.5 },
    ],
    lockOwnBadges: false, lockBadges: [], toolIcons: { Xcode: { id: UUID_A, symbol: 'star.fill', sprite: 'px-gears', plain: true } },
    seenTools: ['Xcode', 'Gradle', 'GitHub Actions'],
  };
}

function fullSnapshot() {
  return {
    mac: 'Hùng’s MacBook "Pro"',
    runs: [{ id: 'r1', agent: 'gemini', title: 'api', detail: 'Running tests', since: T0, icon: { id: UUID_A, symbol: 'star.fill', sprite: 'px-gears', plain: true } }],
    asks: [
      { id: 'a1', pid: 4242, session: 'NotchTasks', tool: 'Bash', summary: 'rm -rf build', questions: [], at: T0, agent: 'claude',
        icon: { id: UUID_B, symbol: 'star.fill', sprite: 'px-cat', plain: true } },
      { id: 'a2', pid: 1, session: 'app', tool: 'AskUserQuestion', summary: 'Which DB?',
        questions: [{ question: 'Which DB?', header: 'DB', options: ['SQLite', 'JSON'], multiSelect: false }], at: T0, agent: 'codex' },
    ],
    limits: [{ provider: 'Claude', label: '5h', used: 72, resets: new Date(T0.getTime() + 3600000) }],
    at: T0,
    builds: [{ id: 'b1', title: 'Xcode', detail: 'build', since: T0 }],
    health: { battery: 64, charging: false, thermal: 1, temp: 52, fan: 3200, cpu: 45, memory: 70 },
    events: [{ id: 'e1', kind: 'ciFailed', title: 'CI failed', detail: 'deploy', at: T0 }],
    job: { title: 'Xcode', detail: 'build', since: T0, state: 'running', icon: { id: UUID_C, symbol: 'star.fill', sprite: 'px-gears', plain: true } },
    notch: fullNotch(),
    crew: { count: 1, title: 'api', detail: 'x', since: T0, agent: 'gemini', members: [{ agent: 'gemini' }] },
  };
}

const keys = (o) => Object.keys(o).sort();

test('snapshot() has the same keys as the Swift snapshot, at every level it can', () => {
  const j = wire.snapshot(fullSnapshot());
  const sw = openFix('snapshot.box');
  assert.deepEqual(keys(j), keys(sw));
  assert.deepEqual(keys(j.asks[0]), keys(sw.asks[0]));
  assert.deepEqual(keys(j.asks[1]), keys(sw.asks[1]));
  assert.deepEqual(keys(j.limits[0]), keys(sw.limits[0]));
  assert.deepEqual(keys(j.health), keys(sw.health));
  assert.deepEqual(keys(j.events[0]), keys(sw.events[0]));
  assert.deepEqual(keys(j.job), keys(sw.job));
  assert.deepEqual(keys(j.crew), keys(sw.crew));
  assert.deepEqual(keys(j.crew.members[0]), keys(sw.crew.members[0]));
  assert.deepEqual(keys(j.notch), keys(sw.notch));
  assert.equal(j.at, T0_SWIFT, 'dates are Swift seconds');
  assert.equal(j.limits[0].resets, T0_SWIFT + 3600);
});

test('snapshot() refuses an agent the phone does not know, and a missing notch field', () => {
  const s = fullSnapshot();
  s.runs[0].agent = 'emacs';
  assert.throws(() => wire.snapshot(s), /unknown agent/);
  const n = fullSnapshot();
  delete n.notch.volume;
  assert.throws(() => wire.snapshot(n), /notch: missing volume/);
});

test('snapshot() drops nil optionals: no icon, job.ended, health when absent', () => {
  const j = wire.snapshot({ mac: 'M', runs: [], asks: [], limits: [], at: T0, events: [], builds: [] });
  assert.deepEqual(Object.keys(j).sort(), ['asks', 'at', 'builds', 'events', 'limits', 'mac', 'runs']);
  assert.equal(j.at, T0_SWIFT);
});

test('askForPush trims like AgentAsk.forPush and still fits a push', () => {
  const longAsk = {
    id: 'a', pid: 1, session: 's', tool: 'Bash', summary: 'x'.repeat(5000) + 'é',
    questions: [
      { question: 'Q'.repeat(250), header: 'H', options: ['1'.repeat(100), '2', '3', '4', '5', '6'], multiSelect: true },
      { question: 'b', options: ['x'], multiSelect: false },
      { question: 'c', options: ['y'], multiSelect: false },
    ],
    at: T0, agent: 'codex',
  };
  const p = wire.askForPush(longAsk);
  assert.equal(p.summary.length, 300);
  assert.equal(p.questions.length, 2);
  assert.equal(p.questions[0].question.length, 200);
  assert.equal(p.questions[0].options.length, 4);
  assert.equal(p.questions[0].options[0].length, 60);
  assert.equal(p.questions[0].header, 'H');
  assert.equal(p.questions[0].multiSelect, true);
  assert.equal(p.agent, 'codex');
  assert.ok(box.seal(wire.ask(p), KEY).length < 3000, 'the push box fits APNs');
  assert.equal(wire.prefixChars('é'.repeat(301), 300).length, 600, 'counts characters, not code units');
});

test('the js-* fixtures the Swift check opens are written here (build/crosscheck)', () => {
  fs.mkdirSync(OUT, { recursive: true });
  const sealedSnapshot = box.seal(wire.snapshot(fullSnapshot()), KEY);
  assert.deepEqual(box.open(sealedSnapshot, KEY), wire.snapshot(fullSnapshot()), 'JS round trip of the full snapshot');
  fs.writeFileSync(path.join(OUT, 'js-snapshot.box'), sealedSnapshot);
  fs.writeFileSync(path.join(OUT, 'js-frame.b64'), box.frame(Buffer.from(sealedSnapshot, 'base64')).toString('base64'));
  const longAsk = {
    id: 'js-a1', pid: 4242, session: 's', tool: 'Bash', summary: 'ls ' + 'y'.repeat(5000),
    questions: [
      { question: 'Q'.repeat(250), header: 'H', options: ['1'.repeat(100), '2', '3', '4', '5', '6'], multiSelect: false },
      { question: 'second', options: ['a'], multiSelect: false },
      { question: 'third', options: ['b'], multiSelect: false },
    ],
    at: T0, agent: 'opencode',
  };
  fs.writeFileSync(path.join(OUT, 'js-ask.box'), box.seal(wire.ask(wire.askForPush(longAsk)), KEY));
  const event = { id: 'js-e1', kind: 'taskDone', title: 'Done', detail: 'build', at: toSwiftDate(T0) };
  fs.writeFileSync(path.join(OUT, 'js-event.box'), box.seal(event, KEY));
  assert.ok(fs.existsSync(path.join(OUT, 'js-snapshot.box')));
});
