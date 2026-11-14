'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const net = require('node:net');
const path = require('node:path');
const box = require('../src/core/box');
const { createSync, crewSignal, bare } = require('../src/core/sync');

const FIX = path.join(__dirname, 'fixtures', 'crosscheck');
const fixture = (name) => fs.readFileSync(path.join(FIX, name), 'utf8').trim();
const K = fixture('key.txt');                       // the Swift fixtures' group key (base64url, 32 bytes)
const KEY = box.fromB64url(K);
const G = 'group123456789012';
const T0 = 1_800_000_000_000;                        // a fixed clock for the tests (ms)
const PRO_UNTIL = 4_000_000_000_000;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function waitFor(cond, ms = 2000) {
  const end = Date.now() + ms;
  while (!cond()) {
    if (Date.now() > end) throw new Error('timed out waiting');
    await sleep(5);
  }
}

/** A Firebase stand-in: anonymous sign-up, token refresh, the group's paths, and the answers event stream. */
function fakeFirebase(opts = {}) {
  const calls = [];
  const state = { ctrl: null, entitlement: opts.entitlement ?? null, members: {} };
  const enc = new TextEncoder();
  const json = (o, status = 200) => new Response(JSON.stringify(o), { status, headers: { 'Content-Type': 'application/json' } });
  const fetch = async (url, init = {}) => {
    const method = init.method || 'GET';
    let body;
    if (typeof init.body === 'string') { try { body = JSON.parse(init.body); } catch { body = init.body; } }
    calls.push({ method, url, body });
    if (init.headers && init.headers.Accept === 'text/event-stream') {
      const stream = new ReadableStream({ start(c) { state.ctrl = c; } });
      if (init.signal) init.signal.addEventListener('abort', () => { try { state.ctrl.error(new Error('aborted')); } catch {} });
      return new Response(stream, { status: 200 });
    }
    if (url.includes('accounts:signUp')) return json({ idToken: 'tok-1', localId: 'uid-mac', refreshToken: 'ref-1' });
    if (url.includes('securetoken')) return json({ id_token: 'tok-2' });
    if (url.includes('/entitlement.json')) return json(state.entitlement);
    if (url.includes('/grants.json') || url.includes('/proNote.json')) return json(null);
    if (url.includes('/members.json') && method === 'GET') return json(state.members);
    return new Response('null', { status: 200 });
  };
  fetch.enc = enc;
  return { fetch, calls, state, enc };
}

function fakeMdns() {
  const published = [];
  return { published, publish(o) { published.push(o); return {}; }, unpublishAll() {}, destroy() {} };
}

function makeSync(o = {}) {
  const fb = fakeFirebase(o.fb);
  const mdns = fakeMdns();
  const answers = [];
  const states = [];
  let saved;
  const clock = o.clock || { t: T0 };
  const sync = createSync({
    apiKey: 'test-key', db: 'https://db.test', fetch: fb.fetch, macName: 'Test PC',
    loadGroup: () => o.group ?? null, saveGroup: (g) => { saved = g; },
    now: () => clock.t, isAway: () => o.away ?? false,
    onAnswer: (a) => answers.push(a), onState: (s) => states.push(s),
    createMdns: () => mdns, helloTimeoutMs: o.helloMs ?? 5000,
  });
  return { sync, fb, mdns, answers, states, clock, saved: () => saved };
}

const groupFixture = () => ({ g: G, k: K, uid: 'uid-mac', refresh: 'ref-1' });

function input(extra = {}) {
  return { runs: [], builds: [], asks: [], limits: [], health: null, notch: null, ...extra };
}

const ask = (over = {}) => ({
  id: 'a1', pid: 42, session: 's1', tool: 'Bash', summary: 'ls -la', questions: [], at: new Date(T0), agent: 'claude', ...over,
});

const isSnap = (c) => c.method === 'PUT' && c.url.includes('/snapshot.json');

test('pair signs up anonymously, writes the Mac member, returns a code parsePairing accepts', async () => {
  const { sync, fb, saved } = makeSync();
  const { code, url } = await sync.pair();
  const p = box.parsePairing(code);
  assert.ok(p, 'code parses');
  assert.equal(p.n, 'Test PC');
  assert.deepEqual(box.parsePairing(url), p, 'URL carries the same code');
  assert.ok(fb.calls.some((c) => c.url.includes('accounts:signUp')), 'anonymous sign-up');
  const put = fb.calls.find((c) => c.method === 'PUT' && c.url.includes(`/groups/${p.g}/members/uid-mac.json`));
  assert.ok(put, 'member written under the new group');
  assert.equal(put.body.role, 'mac');
  assert.equal(put.body.name, 'Test PC');
  assert.deepEqual(put.body.at, { '.sv': 'timestamp' });
  assert.equal(saved().uid, 'uid-mac');
  assert.equal(sync.group().g, p.g);
  assert.equal((await sync.pair()).code, code, 'a second pair shows the same group');
  await sync.stop();
});

test('update writes a sealed snapshot, does not rewrite the same state, writes at once when asks change', async () => {
  const { sync, fb } = makeSync({ fb: { entitlement: { pro: true, until: PRO_UNTIL } }, group: groupFixture() });
  await sync.update(input({ runs: [{ id: 'r1', agent: 'claude', title: 'api', detail: 'Đang chạy' }] }));
  await waitFor(() => fb.calls.some(isSnap));
  const first = fb.calls.find(isSnap);
  const opened = box.open(first.body.box, KEY);
  assert.equal(opened.mac, 'Test PC');
  assert.equal(opened.runs[0].title, 'api');
  assert.equal(first.body.asks, 0);
  await sleep(30);

  await sync.update(input({ runs: [{ id: 'r1', agent: 'claude', title: 'api', detail: 'Đang chạy' }] }));
  await sleep(30);
  assert.equal(fb.calls.filter(isSnap).length, 1, 'same state: no second write');

  await sync.update(input({ runs: [{ id: 'r1', agent: 'claude', title: 'api', detail: 'Đang chạy' }], asks: [ask()] }));
  await waitFor(() => fb.calls.filter(isSnap).length === 2);
  const second = fb.calls.filter(isSnap)[1].body;
  assert.equal(second.asks, 1, 'asks count is readable');
  assert.equal(second.newest, T0, 'newest ask time in ms');
  assert.equal(box.open(second.push, KEY).summary, 'ls -la', 'push box opens with the key');
  await sync.stop();
});

test('without Pro the sealed parts stay empty; asks still go', async () => {
  const { sync, fb } = makeSync({ group: groupFixture() });
  await sync.update(input({ asks: [ask()], runs: [{ id: 'r1', agent: 'claude', title: 'api', detail: 'x' }] }));
  await waitFor(() => fb.calls.some(isSnap));
  const body = fb.calls.find(isSnap).body;
  assert.equal(body.box, '');
  assert.equal(body.crew, '');
  assert.equal(body.crewCount, 0);
  assert.equal(body.asks, 1);
  assert.notEqual(body.push, '');
  await sync.stop();
});

test('non-ask changes wait for a minute, with Pro', async () => {
  const clock = { t: T0 };
  const { sync, fb } = makeSync({ fb: { entitlement: { pro: true, until: PRO_UNTIL } }, group: groupFixture(), clock });
  const run = (id) => ({ id, agent: 'claude', title: 'api', detail: 'x' });
  await sync.update(input({ runs: [run('r1')] }));
  await waitFor(() => fb.calls.filter(isSnap).length === 1);
  await sleep(30);

  clock.t += 1000;
  await sync.update(input({ runs: [run('r1'), run('r2')] })); // a second agent starts: crew changes
  await sleep(30);
  assert.equal(fb.calls.filter(isSnap).length, 1, 'within the minute: waits');

  clock.t += 60_000;
  await sync.update(input({ runs: [run('r1'), run('r2')] }));
  await waitFor(() => fb.calls.filter(isSnap).length === 2);
  assert.equal(box.open(fb.calls.filter(isSnap)[1].body.box, KEY).crew.count, 2);
  await sync.stop();
});

test('an answer from the event stream reaches onAnswer and its path is deleted', async () => {
  const { sync, fb, answers } = makeSync({ group: groupFixture() });
  await sync.start();
  await waitFor(() => fb.state.ctrl);
  const ev = `event: put\ndata: ${JSON.stringify({ path: '/ans1', data: { box: fixture('swift-allow.box'), at: T0 } })}\n\n`;
  fb.state.ctrl.enqueue(fb.enc.encode(ev));
  await waitFor(() => answers.length === 1);
  assert.equal(answers[0].ask, 'a1');
  assert.deepEqual(answers[0].reply, { kind: 'allow' });
  await waitFor(() => fb.calls.some((c) => c.method === 'DELETE' && c.url.includes('/groups/group123456789012/answers/ans1.json')));
  await sync.stop();
});

test('an answer older than 590 s is ignored, but still deleted', async () => {
  const { sync, fb, answers } = makeSync({ group: groupFixture() });
  await sync.start();
  await waitFor(() => fb.state.ctrl);
  const old = T0 - 600_000;
  fb.state.ctrl.enqueue(fb.enc.encode(`event: patch\ndata: ${JSON.stringify({ path: '/old1', data: { box: fixture('swift-allow.box'), at: old } })}\n\n`));
  await waitFor(() => fb.calls.some((c) => c.method === 'DELETE' && c.url.includes('/answers/old1.json')));
  assert.equal(answers.length, 0);
  await sync.stop();
});

test('LAN: a sealed hello proves the key, lanPhones counts it, snapshots arrive as frames', async () => {
  const { sync, fb, mdns, states } = makeSync({ group: groupFixture() });
  await sync.start();
  await sync.update(input({ runs: [{ id: 'r1', agent: 'claude', title: 'api', detail: 'x' }] }));
  const port = sync.lanPort();
  assert.ok(port > 0, 'listening on a free port');
  assert.deepEqual(mdns.published[0], { name: box.serviceName(G), type: 'notchtasks', protocol: 'tcp', port });

  const sock = net.connect(port, '127.0.0.1');
  await new Promise((r) => sock.once('connect', r));
  const hello = { ask: '', reply: { pass: {} }, at: 816072800.25, phone: 'uid-1' };
  sock.write(box.frame(Buffer.from(box.seal(hello, KEY), 'base64')));

  const reader = new box.FrameReader();
  const frames = [];
  sock.on('data', (d) => {
    for (const f of reader.push(d)) frames.push(JSON.parse(box.openBytes(f, KEY).toString('utf8')));
  });
  await waitFor(() => frames.length >= 1);
  assert.equal(frames[0].mac, 'Test PC');
  assert.equal(frames[0].runs[0].title, 'api');
  assert.equal(states.at(-1).lanPhones, 1);
  assert.equal(fb.calls.filter(isSnap).length, 1, 'no Firebase write while a phone is on the Wi-Fi');

  sock.destroy();
  await waitFor(() => states.at(-1).lanPhones === 0);
  await sync.stop();
});

test('LAN: a connection with no hello is dropped after the hello timeout', async () => {
  const { sync } = makeSync({ group: groupFixture(), helloMs: 100 });
  await sync.start();
  const sock = net.connect(sync.lanPort(), '127.0.0.1');
  const closed = new Promise((r) => sock.once('close', r));
  await new Promise((r) => sock.once('connect', r));
  await closed;
  await sync.stop();
});

test('LAN: a bad frame drops the connection', async () => {
  const { sync } = makeSync({ group: groupFixture() });
  await sync.start();
  const sock = net.connect(sync.lanPort(), '127.0.0.1');
  await new Promise((r) => sock.once('connect', r));
  const closed = new Promise((r) => sock.once('close', r));
  sock.write(box.frame(Buffer.alloc(40, 1)));
  await closed;
  await sync.stop();
});

test('crewSignal: starting and all stopping at once, a count change at most every 10 minutes', () => {
  const t0 = new Date(0);
  const idle = { count: 0, at: 0, stamp: -Infinity };
  const on = crewSignal(1, 5, idle, t0);
  assert.equal(on.count, 1);
  assert.equal(crewSignal(3, 5, on, t0.getTime() + 599_000).count, 1);
  assert.equal(crewSignal(3, 5, on, t0.getTime() + 600_000).count, 3);
  assert.equal(crewSignal(0, 0, on, t0.getTime() + 1000).count, 0);
});

test('bare strips the clock and the estimate from a detail', () => {
  assert.equal(bare('clean · NotchTasks · 0:04 · ≈ 40s nữa'), 'clean · NotchTasks');
  assert.equal(bare('Đang làm việc · 9 phút'), 'Đang làm việc');
});
