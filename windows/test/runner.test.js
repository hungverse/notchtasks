'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const R = require('../src/core/runnerlog');
const { L } = require('../src/core/l10n');

const FIX = path.join(__dirname, 'fixtures', 'builds');
const readFix = (n) => fs.readFileSync(path.join(FIX, n), 'utf8');

test('GitLab Runner log: running, succeeded and failed jobs with their pages', () => {
  const r = R.parseGitLab(readFix('gitlab-runner.log'), { server: 'https://gitlab.com' });
  // job 101 received then succeeded: not running; job 102 received then failed: not running
  assert.deepEqual(r.running, []);
  assert.deepEqual([...r.succeeded], ['101']);
  assert.equal(r.failed.length, 1);
  const f = r.failed[0];
  assert.equal(f.id, '102');
  assert.equal(f.name, 'job 102');                                    // job_name is not in the failed line: falls back
  assert.equal(f.reason, 'script_failure · exit 1');
  assert.equal(f.project, 'shop/web');
  assert.equal(f.url, 'https://gitlab.com/shop/web/-/jobs/102');
});

test('GitLab Runner log: a job still received is running; repo_url gives the project path', () => {
  const text = 'Feb 10 10:00:01 received job=7 job_name="unit tests" repo_url="https://gitlab.com/shop/api.git"\n';
  const r = R.parseGitLab(text, { server: 'https://gl.example.com' });
  assert.equal(r.running.length, 1);
  assert.deepEqual(r.running[0], {
    id: '7', name: 'unit tests', project: 'shop/api', url: 'https://gl.example.com/shop/api/-/jobs/7',
  });
  assert.equal(r.failed.length, 0);
});

test('GitLab Runner log: ANSI colour codes in the line do not break fields', () => {
  const text = '\u001b[0;m\u001b[0;mJob failed job=9 job_name="\u001b[31mtest\u001b[0m" project_full_path="a/b" exit_code=2\n';
  const r = R.parseGitLab(text);
  assert.equal(r.failed[0].reason, 'exit 2');
  assert.equal(r.failed[0].project, 'a/b');
});

test('GitHub self-hosted worker log: job name and result of the last job', () => {
  const log = readFix('runner-worker.log');
  assert.equal(R.workerJobName(log), 'build (windows)');
  assert.equal(R.workerResult(log), 'Succeeded');
  assert.equal(R.workerResult('[t] Starting Worker\n'), null, 'still running: no result line');
  assert.equal(R.repoFromWorkerLog(log), 'app');
});

test('runner folder: _diag is next to bin, Windows separators kept', () => {
  assert.equal(R.diagFromWorkerExe('C:\\actions-runner\\bin\\Runner.Worker.exe'), 'C:\\actions-runner\\_diag');
  assert.equal(R.diagFromWorkerExe('/home/u/actions-runner/bin/Runner.Worker'), '/home/u/actions-runner/_diag');
  assert.equal(R.DEFAULT_RUNNER_DIR, 'C:\\actions-runner');
});

test('newest Worker_ log in _diag, and a worker job row from it', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'nt-runner-'));
  try {
    const diag = path.join(root, '_diag');
    fs.mkdirSync(diag);
    fs.writeFileSync(path.join(diag, 'Runner_1.log'), 'runner');
    fs.writeFileSync(path.join(diag, 'Worker_old.log'), 'old job');
    const old = new Date(Date.now() - 600000);
    fs.utimesSync(path.join(diag, 'Worker_old.log'), old, old);
    fs.copyFileSync(path.join(FIX, 'runner-worker.log'), path.join(diag, 'Worker_new.log'));
    assert.equal(path.basename(R.newestWorkerLog(diag)), 'Worker_new.log');
    assert.equal(R.newestWorkerLog(path.join(root, 'missing')), null);

    const exe = path.join(root, 'bin', 'Runner.Worker.exe');
    const row = R.workerJob({ pid: 77, exe });
    assert.equal(row.id, 'ghw:77');
    assert.equal(row.title, 'app');
    assert.equal(row.detail, 'build (windows)');
    assert.equal(row.tool, 'GitHub Actions');
    assert.equal(row.working, true);
    assert.equal(L('Đang chờ runner').length > 0, true);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
