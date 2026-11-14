'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const G = require('../src/core/github');
const { L } = require('../src/core/l10n');

const FIX = path.join(__dirname, 'fixtures', 'builds');
const readJson = (n) => JSON.parse(fs.readFileSync(path.join(FIX, n), 'utf8'));

test('repo from the remote URL: ssh, https, .git suffix, junk', () => {
  assert.equal(G.repoFromRemote('git@github.com:acme/app.git'), 'acme/app');
  assert.equal(G.repoFromRemote('https://github.com/acme/app'), 'acme/app');
  assert.equal(G.repoFromRemote('https://github.com/acme/app/'), 'acme/app');
  assert.equal(G.repoFromRemote('https://gitlab.com/acme/app.git'), null);
  assert.equal(G.repoFromRemote('https://github.com/acme'), null);
});

test('.git/config and HEAD: origin repo and the checked-out branch', () => {
  const config = '[core]\r\n\tbare = false\r\n[remote "upstream"]\r\n\turl = https://github.com/other/x.git\r\n' +
    '[remote "origin"]\r\n\turl = git@github.com:acme/app.git\r\n\tfetch = +refs/heads/*:refs/remotes/origin/*\r\n';
  assert.deepEqual(G.parse(config, 'ref: refs/heads/feature/login\r\n'), { repo: 'acme/app', branch: 'feature/login' });
  assert.equal(G.parse(config, 'a1b2c3d4e5f6\n'), null, 'detached HEAD is not a branch');
  assert.equal(G.parse('[remote "origin"]\n\turl = https://example.com/a/b\n', 'ref: refs/heads/main\n'), null);
});

test('checkout() finds the repo from a sub-folder; worktree (.git file) is skipped', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'nt-gh-'));
  try {
    const repo = path.join(root, 'app');
    fs.mkdirSync(path.join(repo, '.git'), { recursive: true });
    fs.mkdirSync(path.join(repo, 'src', 'deep'), { recursive: true });
    fs.writeFileSync(path.join(repo, '.git', 'config'), '[remote "origin"]\n\turl = https://github.com/acme/app.git\n');
    fs.writeFileSync(path.join(repo, '.git', 'HEAD'), 'ref: refs/heads/main\n');
    assert.deepEqual(G.checkout(path.join(repo, 'src', 'deep')), { repo: 'acme/app', branch: 'main' });
    const wt = path.join(root, 'wt');
    fs.mkdirSync(wt);
    fs.writeFileSync(path.join(wt, '.git'), 'gitdir: ../app/.git/worktrees/wt\n');
    assert.equal(G.checkout(wt), null);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('workflow_runs JSON -> GHRun (ids, repo, times, status, URL)', () => {
  const runs = G.runs(readJson('gh-runs.json'));
  assert.equal(runs.length, 2);
  const ci = runs[0];
  assert.equal(ci.id, 9001);
  assert.equal(ci.repo, 'acme/app');
  assert.equal(ci.workflow, 'CI');
  assert.equal(ci.title, 'Fix login redirect');
  assert.equal(ci.status, 'in_progress');
  assert.equal(ci.conclusion, null);
  assert.equal(ci.url, 'https://github.com/acme/app/actions/runs/9001');
  assert.equal(ci.started, Date.parse('2026-11-10T03:20:05Z'), 'run_started_at wins over created_at');
  assert.equal(runs[1].conclusion, 'success');
  assert.deepEqual(G.runs(null), []);
  assert.deepEqual(G.runs({ workflow_runs: [{ id: 'x' }] }), [], 'a run without repository is dropped');
});

test('run jobs JSON -> current step and step progress', () => {
  const s = G.steps(readJson('gh-run-jobs.json'));
  assert.equal(s.step, 'build (windows) › Build installer');
  assert.equal(s.progress, 5 / 7);  // 5 completed of 7 steps over both jobs
  assert.deepEqual(G.steps(null), { step: null, progress: null });
});

test('GitHubWatcher: settings token wins; jobs row for a run in progress', async () => {
  const w = new G.GitHubWatcher({ now: () => Date.parse('2026-11-10T03:21:05Z') });
  w.runs = new Map(G.runs(readJson('gh-runs.json')).map((r) => [r.id, { ...r, step: 'Build', progress: 0.4 }]));
  // a token from settings is used without asking gh
  assert.equal(await w.token('  ghp_settings  '), 'ghp_settings');
  const [row] = w.jobs;
  assert.equal(row.id, 'gh:9001');
  assert.equal(row.title, 'acme/app · CI');
  assert.equal(row.detail, 'Build · 1:00');
  assert.equal(row.progress, 0.4);
  assert.equal(row.url, 'https://github.com/acme/app/actions/runs/9001');
  assert.equal(row.tool, 'GitHub Actions');
});

test('GitHubWatcher: queued run says it waits for a runner', () => {
  const w = new G.GitHubWatcher();
  w.runs = new Map([[1, { id: 1, repo: 'a/b', workflow: 'CI', status: 'queued', started: 0, url: null, progress: null }]]);
  assert.equal(w.jobs[0].detail, L('Đang chờ runner'));
});

test('GitHubWatcher.pushed records the checkout only', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'nt-gh2-'));
  try {
    const w = new G.GitHubWatcher();
    assert.equal(w.pushed(root), false, 'no repo here');
    const repo = path.join(root, 'r');
    fs.mkdirSync(path.join(repo, '.git'), { recursive: true });
    fs.writeFileSync(path.join(repo, '.git', 'config'), '[remote "origin"]\n\turl = git@github.com:acme/r.git\n');
    fs.writeFileSync(path.join(repo, '.git', 'HEAD'), 'ref: refs/heads/dev\n');
    assert.equal(w.pushed(repo, 1000), true);
    assert.deepEqual(w.watched.get('acme/r'), { branch: 'dev', since: 1000 });
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
