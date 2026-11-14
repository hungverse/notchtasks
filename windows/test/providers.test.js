'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');

const P = require('../src/core/providers');

const made = [];
process.on('exit', () => made.forEach((d) => fs.rmSync(d, { recursive: true, force: true })));
const tmp = () => { const d = fs.mkdtempSync(path.join(os.tmpdir(), 'nt-prov-')); made.push(d); return d; };
const iso = (s) => new Date(s).toISOString();

// ---- Limits and labels

test('windowLabel: hours under a day, days after', () => {
  assert.equal(P.windowLabel(300), '5h');
  assert.equal(P.windowLabel(10080), '7d');
});

test('limitStep: 5 % steps, then 99 (last call) and 100 (out)', () => {
  assert.equal(P.limitStep(84), 80);
  assert.equal(P.limitStep(85), 85);
  assert.equal(P.limitStep(97), 95);
  assert.equal(P.limitStep(99.4), 99);
  assert.equal(P.limitStep(100), 100);
});

test('resetText: time today, weekday within a week, else the date (UTC, en-GB)', () => {
  const now = new Date('2026-11-11T06:53:00Z');
  const opt = { now, locale: 'en-GB', timeZone: 'UTC' };
  assert.equal(P.resetText({ resets: new Date('2026-11-11T07:21:06Z') }, opt), '07:21');
  assert.equal(P.resetText({ resets: new Date('2026-11-14T12:19:38Z') }, opt), 'Sat 12:19');
  assert.equal(P.resetText({ resets: new Date(now.getTime() + 20 * 86400e3) }, opt), '1 Dec');
});

// ---- Antigravity quotas

const ANTIGRAVITY = '{"userStatus":{"cascadeModelConfigData":{"clientModelConfigs":['
  + '{"label":"Gemini 3.8 Flash (High)","quotaInfo":{"remainingFraction":0.25,"resetTime":"2026-11-11T07:21:06Z"}},'
  + '{"label":"Gemini 3.8 Flash (Low)","quotaInfo":{"remainingFraction":0.25,"resetTime":"2026-11-11T07:21:06Z"}},'
  + '{"label":"Claude Sonnet 4.6","quotaInfo":{"remainingFraction":1,"resetTime":"2026-11-14T12:19:38Z"}},'
  + '{"label":"GPT-OSS 120B","quotaInfo":{"remainingFraction":1,"resetTime":"2026-11-14T12:19:38Z"}},'
  + '{"label":"Gemini 4 Argon","quotaInfo":{}}]}}}';

test('antigravity quotas: models sharing a reset and use are one window, fullest first', () => {
  const now = new Date('2026-11-11T06:53:00Z');
  const p = P.parseAntigravityLimits(ANTIGRAVITY, now);
  assert.equal(p.name, 'Antigravity');
  assert.deepEqual(p.windows.map((w) => w.label), ['Gemini', 'Claude/GPT-OSS']);
  assert.equal(p.windows[0].used, 75);
  assert.equal(p.windows[1].used, 0);
  assert.equal(iso(p.windows[0].resets), '2026-11-11T07:21:06.000Z');
});

test('antigravity quota that already reset counts as unused; no quotas = null', () => {
  const later = new Date('2026-11-12T00:00:00Z');
  assert.equal(P.parseAntigravityLimits(ANTIGRAVITY, later).windows[0].used, 0);
  assert.equal(P.parseAntigravityLimits('{"userStatus":{}}', later), null);
  assert.equal(P.parseAntigravityLimits('not json', later), null);
});

test('fetchAntigravityLimits asks the live language server named in the daemon folder', async () => {
  const home = tmp();
  const daemon = path.join(home, '.gemini', 'antigravity', 'daemon');
  fs.mkdirSync(daemon, { recursive: true });
  let seenToken = null;
  const server = http.createServer((req, res) => {
    seenToken = req.headers['x-codeium-csrf-token'];
    res.end(ANTIGRAVITY);
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  try {
    fs.writeFileSync(path.join(daemon, 'hub.json'), JSON.stringify({
      pid: process.pid, httpPort: server.address().port, csrfToken: 'tok-123',
    }));
    const p = await P.fetchAntigravityLimits({ home, now: new Date('2026-11-11T06:53:00Z') });
    assert.deepEqual(p.windows.map((w) => w.label), ['Gemini', 'Claude/GPT-OSS']);
    assert.equal(seenToken, 'tok-123');
  } finally {
    server.close();
  }
  // a daemon whose process is gone is skipped; no daemon folder = null
  fs.writeFileSync(path.join(daemon, 'hub.json'), JSON.stringify({ pid: 4194301, httpPort: 1, csrfToken: 'x' }));
  assert.equal(await P.fetchAntigravityLimits({ home: tmp() }), null);
  assert.equal(await P.fetchAntigravityLimits({ home }), null);
});

// ---- Claude Code

test('claude usage text: 5h and 7d windows, reset times in the zone printed', () => {
  const usage = 'You are currently using your subscription\n\n'
    + 'Current session: 34% used · resets Nov 3 at 8:09pm (Asia/Saigon)\n'
    + 'Current week (all models): 34% used · resets Nov 5 at 1:59pm (Asia/Saigon)\n'
    + 'Current week (Sonnet only): 3% used · resets Nov 5 at 2pm (Asia/Saigon)\n';
  const w = P.parseClaudeUsage(usage, new Date('2026-11-03T10:00:00Z'));
  assert.equal(w.length, 2);
  assert.equal(w[0].label, '5h');
  assert.equal(w[0].used, 34);
  assert.equal(iso(w[0].resets), '2026-11-03T13:09:00.000Z');
  assert.equal(w[1].label, '7d');
  assert.equal(iso(w[1].resets), '2026-11-05T06:59:00.000Z');
});

test('claude usage: a reset without minutes is on the hour; a reset past new year is next year', () => {
  const hourOnly = P.parseClaudeUsage('Current week (all models): 34% used · resets Nov 5 at 2pm (Asia/Saigon)', new Date('2026-11-03T10:58:00Z'));
  assert.equal(iso(hourOnly[0].resets), '2026-11-05T07:00:00.000Z');
  const newYear = P.parseClaudeUsage('Current session: 10% used · resets Jan 2 at 9am (UTC)', new Date('2026-12-31T10:00:00Z'));
  assert.equal(iso(newYear[0].resets), '2027-01-02T09:00:00.000Z');
});

test('claude usage: no percentage, or a line for something else, gives nothing', () => {
  assert.deepEqual(P.parseClaudeUsage('Current session: resets soon', new Date()), []);
  assert.deepEqual(P.parseClaudeUsage('Something else: 5% used', new Date()), []);
});

test('runClaudeUsage runs the CLI and parses what it prints', async () => {
  if (process.platform === 'win32') return;  // the fake CLI is a shell script
  const dir = tmp();
  const cli = path.join(dir, 'claude');
  fs.writeFileSync(cli, '#!/bin/sh\necho "Current session: 34% used · resets Nov 3 at 8:09pm (Asia/Saigon)"\n');
  fs.chmodSync(cli, 0o755);
  const w = await P.runClaudeUsage(new Date('2026-11-03T10:00:00Z'), cli);
  assert.equal(w[0].label, '5h');
  assert.equal(w[0].used, 34);
  assert.deepEqual(await P.runClaudeUsage(new Date(), null), []);
});

test('claudeLimits reads the statusline snapshot; past resets are dropped', () => {
  const dir = tmp();
  const now = new Date('2026-11-14T10:00:00Z');
  const future = now.getTime() / 1000 + 3600;
  const past = now.getTime() / 1000 - 100;
  fs.writeFileSync(path.join(dir, 'notchtasks-status.json'), JSON.stringify({
    rate_limits: {
      five_hour: { used_percentage: 42, resets_at: future },
      seven_day: { used_percentage: 9, resets_at: past },
    },
  }));
  const p = P.claudeLimits(now, { dir });
  assert.equal(p.name, 'Claude');
  assert.deepEqual(p.windows.map((w) => [w.label, w.used]), [['5h', 42]]);
  assert.equal(P.claudeLimits(now, { dir: tmp() }), null);
});

test('claudeSessions: the registry in <claude dir>/sessions; entries without a pid are skipped', () => {
  const dir = tmp();
  fs.mkdirSync(path.join(dir, 'sessions'));
  fs.writeFileSync(path.join(dir, 'sessions', '4321.json'), JSON.stringify({ pid: 4321, sessionId: 'abc', cwd: 'C:\\x\\app', name: 'app work', status: 'busy', statusUpdatedAt: 1 }));
  fs.writeFileSync(path.join(dir, 'sessions', 'bad.json'), JSON.stringify({ sessionId: 'nope' }));
  const list = P.claudeSessions({ dir });
  assert.equal(list.length, 1);
  assert.equal(list[0].pid, 4321);
  assert.equal(list[0].name, 'app work');
  assert.equal(list[0].status, 'busy');
});

test('claudeDir: CLAUDE_CONFIG_DIR wins, else ~/.claude', () => {
  assert.equal(P.claudeDir('/h', {}), path.join('/h', '.claude'));
  assert.equal(P.claudeDir('/h', { CLAUDE_CONFIG_DIR: '/cfg' }), '/cfg');
});

// ---- Codex

const TS = (ms) => new Date(ms).toISOString();

test('codex status: the last task_started / task_complete / turn_aborted decides', () => {
  const started = `{"timestamp":"2026-11-05T03:07:53.935Z","type":"event_msg","payload":{"type":"task_started","turn_id":"t1"}}`;
  const done = `{"timestamp":"2026-11-05T03:09:10.100Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"t1"}}`;
  const s1 = P.parseCodexStatus(`${started}\n{"type":"response_item"}\n`);
  assert.equal(s1.working, true);
  assert.equal(iso(s1.at), '2026-11-05T03:07:53.935Z');
  const s2 = P.parseCodexStatus(`${started}\n${done}\n`);
  assert.equal(s2.working, false);
  assert.equal(P.parseCodexStatus('{"type":"response_item"}\n'), null);
});

test('codex limits: primary and secondary windows; a reset already passed counts as unused', () => {
  const now = new Date('2026-11-14T10:00:00Z');
  const sec = now.getTime() / 1000;
  const line = JSON.stringify({
    timestamp: TS(now.getTime()),
    type: 'event_msg',
    payload: {
      type: 'token_count',
      rate_limits: {
        primary: { used_percent: 42.5, window_minutes: 300, resets_at: sec + 3600 },
        secondary: { used_percent: 12, window_minutes: 10080, resets_at: sec - 100 },
      },
    },
  });
  const r = P.parseCodexLimits(`{"type":"event_msg"}\n${line}\n`, now);
  assert.equal(iso(r.at), TS(now.getTime()));
  assert.deepEqual(r.windows.map((w) => [w.label, w.used]), [['5h', 42.5], ['7d', 0]]);
  assert.equal(P.parseCodexLimits('{"type":"x"}\n', now), null);
});

test('codex question: read until you write back in the thread', () => {
  const q = '{"timestamp":"2026-11-05T03:07:53.935Z","type":"response_item","payload":{"type":"function_call","name":"request_user_input_async","arguments":"{\\"questions\\":[{\\"title\\":\\"Phần nào?\\",\\"options\\":[\\"JSON\\",\\"Cách duyệt\\"]}]}","call_id":"call_1"}}';
  const open = P.parseCodexQuestion(q, 't1', null);
  assert.deepEqual(open.questions[0].options, ['JSON', 'Cách duyệt']);
  assert.equal(open.questions[0].question, 'Phần nào?');
  assert.equal(open.callID, 'call_1');
  const user = '{"timestamp":"2026-11-05T03:09:00.000Z","type":"response_item","payload":{"type":"message","id":"m","role":"user","content":[]}}';
  assert.equal(P.parseCodexQuestion(user, 't1', open), null);
});

test('codex question in the terminal form: {question, header, options: [{label}]}', () => {
  const args = JSON.stringify({ questions: [{ question: 'Color?', header: 'Pick', multiSelect: true, options: [{ label: 'Red' }, { label: 'Blue' }] }] });
  const line = JSON.stringify({ type: 'response_item', payload: { type: 'function_call', name: 'request_user_input', arguments: args, call_id: 'c2' } });
  const q = P.parseCodexQuestion(line, 't2', null);
  assert.deepEqual(q.questions, [{ question: 'Color?', header: 'Pick', options: ['Red', 'Blue'], multiSelect: true }]);
});

function rollout(file, { cwd, sessionId, lines, mtime }) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const head = JSON.stringify({ timestamp: TS(Date.now()), type: 'session_meta', payload: { id: sessionId, cwd, session_id: sessionId } });
  fs.writeFileSync(file, [head, ...lines].join('\n') + '\n');
  if (mtime) fs.utimesSync(file, mtime / 1000, mtime / 1000);
}

test('CodexScanner: a running task shows as a job named by its folder; child threads do not', () => {
  const root = path.join(tmp(), 'sessions');
  const now = new Date();
  const started = JSON.stringify({ timestamp: TS(now.getTime() - 30e3), type: 'event_msg', payload: { type: 'task_started', turn_id: 't' } });
  rollout(path.join(root, '2026', '11', '14', 'rollout-a.jsonl'), { cwd: 'C:\\Users\\hung\\Projects\\notchtasks', sessionId: 'sa', lines: [started] });
  rollout(path.join(root, '2026', '11', '14', 'rollout-child.jsonl'), {
    cwd: 'C:\\Users\\hung\\Projects\\other', sessionId: 'sc',
    lines: [JSON.stringify({ parent_thread_id: 'sa' }), started],
  });
  const scanner = new P.CodexScanner({ root });
  const jobs = scanner.jobs(now);
  assert.equal(jobs.length, 1);
  assert.equal(jobs[0].title, 'notchtasks');
  assert.equal(jobs[0].working, true);
  assert.equal(jobs[0].id, 'rollout-a.jsonl');
});

test('CodexScanner: finished tasks show for 10 min, rollouts idle for 20 min leave', () => {
  const root = path.join(tmp(), 'sessions');
  const now = new Date();
  const done = JSON.stringify({ timestamp: TS(now.getTime() - 60e3), type: 'event_msg', payload: { type: 'task_complete', turn_id: 't' } });
  const file = path.join(root, 'rollout-done.jsonl');
  rollout(file, { cwd: '/x/app', sessionId: 'sd', lines: [done] });
  const scanner = new P.CodexScanner({ root });
  const j = scanner.jobs(now);
  assert.equal(j.length, 1);
  assert.equal(j[0].working, false);
  assert.equal(scanner.jobs(new Date(now.getTime() + 25 * 60e3)).length, 0);
});

test('CodexScanner: newest limits across rollouts, and an open question', () => {
  const root = path.join(tmp(), 'sessions');
  const now = new Date();
  const sec = now.getTime() / 1000;
  const limits = (pct, at) => JSON.stringify({
    timestamp: TS(at), type: 'event_msg',
    payload: { rate_limits: { primary: { used_percent: pct, window_minutes: 300, resets_at: sec + 3600 } } },
  });
  rollout(path.join(root, 'old.jsonl'), { cwd: '/a', sessionId: 'o', lines: [limits(10, now.getTime() - 3600e3)] });
  rollout(path.join(root, 'new.jsonl'), { cwd: '/b', sessionId: 'n', lines: [limits(55, now.getTime() - 60e3)] });
  const q = JSON.stringify({ timestamp: TS(now.getTime()), type: 'response_item', payload: { type: 'function_call', name: 'request_user_input', arguments: JSON.stringify({ questions: [{ title: 'Go?', options: ['Yes'] }] }), call_id: 'q' } });
  rollout(path.join(root, 'q.jsonl'), { cwd: '/q/thread', sessionId: 'qq', lines: [q] });
  const scanner = new P.CodexScanner({ root });
  const l = scanner.limits(now);
  assert.equal(l.name, 'Codex');
  assert.equal(l.windows[0].used, 55);
  scanner.jobs(now);
  const open = scanner.questions(now);
  assert.equal(open.length, 1);
  assert.equal(open[0].question.questions[0].question, 'Go?');
  assert.equal(open[0].title, 'thread', 'title is the last folder of cwd');
});

// ---- Antigravity conversations

test('antigravity mid-turn: running steps, and a tool step the model has yet to answer; a finished reply is not', () => {
  assert.equal(P.antigravityMidTurn(15, 8), true);
  assert.equal(P.antigravityMidTurn(132, 3), true);
  assert.equal(P.antigravityMidTurn(21, 2), true);
  assert.equal(P.antigravityMidTurn(15, 3), false);
  assert.equal(P.antigravityMidTurn(21, 6), false);
  assert.equal(P.antigravityMidTurn(21, 7), false);
});

test('antigravity project: the workspace folder from the metadata blob, varint lengths', () => {
  let short = Buffer.concat([Buffer.from([0x0a, 26]), Buffer.from('file:///Users/x/NotchTasks'), Buffer.from([0x7a])]);
  assert.equal(P.antigravityProject(short), 'NotchTasks');
  const path132 = `file:///${'a'.repeat(121)}/NT`;
  const long = Buffer.concat([Buffer.from([0x0a, 0x84, 0x01]), Buffer.from(path132), Buffer.from([0x7a])]);
  assert.equal(P.antigravityProject(long), 'NT');
  assert.equal(P.antigravityProject(Buffer.from('no uri here')), null);
});

test('AntigravityScanner: running shows, held between steps, a crash left mid-step clears after 10 min', () => {
  const root = tmp();
  const db = path.join(root, 'conv-1.db');
  fs.writeFileSync(db, 'x');
  const t0 = Math.floor(Date.now() / 1000) * 1000;
  const touch = (ms) => fs.utimesSync(db, ms / 1000, ms / 1000);
  touch(t0);
  let state = { active: true, title: 'NotchTasks' };
  const s = new P.AntigravityScanner({ root, readDb: () => state });

  const jobs = s.jobs(new Date(t0));
  assert.equal(jobs.length, 1);
  assert.equal(jobs[0].working, true);
  assert.equal(jobs[0].title, 'NotchTasks');

  // the conversation writes its next step while the agent is between steps: held, not gone
  state = { active: false, title: 'NotchTasks' };
  touch(t0 + 1000);
  assert.equal(s.jobs(new Date(t0 + 1000)).length, 1, 'held between steps');
  assert.equal(s.jobs(new Date(t0 + 60e3)).length, 0, 'done leaves once 45 s pass without work');

  // a step left "generating" by a crash: nothing written for 10 minutes
  state = { active: true, title: 'NotchTasks' };
  touch(t0);
  assert.equal(new P.AntigravityScanner({ root, readDb: () => state }).jobs(new Date(t0 + 11 * 60e3)).length, 0);
});

test('readAntigravityConversation: null for a file that is not a conversation', () => {
  const file = path.join(tmp(), 'junk.db');
  fs.writeFileSync(file, 'not a database');
  assert.equal(P.readAntigravityConversation(file), null);
});

// ---- Local models

test('LM Studio and Ollama lists are parsed', () => {
  assert.deepEqual(P.parseLMStudio('[{"displayName":"Gemma 4 26B","identifier":"g"}]'), ['Gemma 4 26B']);
  assert.deepEqual(P.parseLMStudio('[{"identifier":"only-id"}]'), ['only-id']);
  assert.deepEqual(P.parseLMStudio('nope'), []);
  assert.deepEqual(P.parseOllama('{"models":[{"name":"qwen3:1.7b"}]}'), ['qwen3:1.7b']);
  assert.deepEqual(P.parseOllama('{}'), []);
});

// ---- Finding a CLI

test('findCli: searches PATH and the per-user folders, not files that are not runnable', () => {
  if (process.platform === 'win32') return;
  const bin = tmp();
  const cli = path.join(bin, 'fakecli');
  fs.writeFileSync(cli, '#!/bin/sh\n');
  fs.chmodSync(cli, 0o755);
  fs.writeFileSync(path.join(bin, 'notexec'), 'x');
  assert.equal(P.findCli('fakecli', { env: { PATH: bin }, home: tmp(), platform: 'linux' }), cli);
  assert.equal(P.findCli('notexec', { env: { PATH: bin }, home: tmp(), platform: 'linux' }), null);
  assert.equal(P.findCli('fakecli', { env: { PATH: '' }, home: tmp(), platform: 'linux' }), null);
});

test('findCli on Windows: PATH with .exe/.cmd/.bat/.ps1, and %APPDATA%\\npm', () => {
  const dir = tmp();
  fs.writeFileSync(path.join(dir, 'codex.cmd'), '@echo off\r\n');
  fs.mkdirSync(path.join(dir, 'npm'));
  const appdata = tmp();
  fs.mkdirSync(path.join(appdata, 'npm'));
  fs.writeFileSync(path.join(appdata, 'npm', 'gemini.ps1'), '');
  const env = { PATH: dir, APPDATA: appdata };
  assert.equal(P.findCli('codex', { env, home: tmp(), platform: 'win32' }), path.join(dir, 'codex.cmd'));
  assert.equal(P.findCli('gemini', { env, home: tmp(), platform: 'win32' }), path.join(appdata, 'npm', 'gemini.ps1'));
  assert.equal(P.findCli('agy', { env, home: tmp(), platform: 'win32' }), null);
});

// ---- Small helpers

test('lastComponent: both separators, trailing separators ignored', () => {
  assert.equal(P.lastComponent('C:\\Users\\hung\\Projects\\app\\'), 'app');
  assert.equal(P.lastComponent('/Users/dev/app'), 'app');
  assert.equal(P.lastComponent(''), '');
});

test('ago: seconds, minutes, hours', () => {
  assert.match(P.ago(30), /30/);
  assert.match(P.ago(120), /2/);
  assert.match(P.ago(7200), /2/);
});
