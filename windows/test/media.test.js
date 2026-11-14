'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const { PassThrough, Writable } = require('node:stream');
const { createMedia, parseSmtcLine, commandLine, SCRIPT } = require('../src/core/media');

test('a JSON line becomes a NowPlaying; zero duration is no duration', () => {
  const np = parseSmtcLine('{"title":"Song","artist":"Band","album":"Record","app":"Spotify.exe","playing":true,"position":12,"duration":200}');
  assert.deepEqual(np, { source: 'Spotify.exe', title: 'Song', artist: 'Band', album: 'Record', playing: true,
    position: 12, duration: 200, artwork: null });
  assert.equal(parseSmtcLine('{"title":"x","app":"a","playing":false,"duration":0}').duration, null);
});

test('{"none":true} means nothing plays; other text is ignored', () => {
  assert.equal(parseSmtcLine('{"none":true}'), null);
  assert.equal(parseSmtcLine('WARNING: something'), undefined);
  assert.equal(parseSmtcLine('[1,2]'), undefined);
});

test('artwork arrives with a track and is kept while the track stays the same', () => {
  const first = parseSmtcLine('{"title":"A","artist":"X","app":"s","playing":true,"artBase64":"QUJD","artMime":"image/png"}');
  assert.equal(first.artwork, 'data:image/png;base64,QUJD');
  // same track, position only: no art in the line, the artwork stays
  const second = parseSmtcLine('{"title":"A","artist":"X","app":"s","playing":true,"position":5}', first);
  assert.equal(second.artwork, first.artwork);
  // a new track without art: no artwork
  assert.equal(parseSmtcLine('{"title":"B","artist":"X","app":"s","playing":true}', first).artwork, null);
});

test('commands map to stdin lines; anything else is refused', () => {
  assert.equal(commandLine('toggle'), 'toggle');
  assert.equal(commandLine('next'), 'next');
  assert.equal(commandLine('previous'), 'previous');
  assert.equal(commandLine({ seek: 30.5 }), 'seek 30.5');
  assert.equal(commandLine({ seek: -1 }), null);
  assert.equal(commandLine('rewind'), null);
});

// A fake powershell child: stdin collects what media.js writes, stdout is driven by the test.
function fakeSpawn() {
  const spawned = [];
  const spawn = (cmd, args) => {
    const proc = new EventEmitter();
    proc.stdout = new PassThrough();
    proc.stdin = new Writable({ write(chunk, enc, cb) { proc.written = (proc.written || '') + chunk; cb(); } });
    proc.kill = () => { proc.killed = true; proc.emit('exit', null); };
    spawned.push({ cmd, args, proc });
    return proc;
  };
  return { spawned, spawn };
}

test('on Windows: one PowerShell child with the script, commands written, lines published', async () => {
  const { spawned, spawn } = fakeSpawn();
  const got = [];
  const media = createMedia({ platform: 'win32', spawn, onChange: (np) => got.push(np) });
  media.start();
  assert.equal(spawned.length, 1);
  assert.equal(spawned[0].cmd, 'powershell.exe');
  assert.deepEqual(spawned[0].args, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', SCRIPT]);

  assert.equal(media.command('next'), true);
  assert.equal(media.command({ seek: 12 }), true);
  assert.equal(media.command('bogus'), false);
  assert.equal(spawned[0].proc.written, 'next\nseek 12\n');

  media.setSlow(true);
  assert.equal(spawned[0].proc.written.endsWith('slow\n'), true);

  spawned[0].proc.stdout.write('{"title":"Hi","app":"chrome.exe","playing":false}\n');
  await new Promise((r) => setImmediate(r));
  assert.equal(got.length, 1);
  assert.equal(got[0].title, 'Hi');
  assert.equal(media.current().source, 'chrome.exe');

  media.stop();
  assert.equal(spawned[0].proc.killed, true);
  assert.equal(media.current(), null);
  assert.equal(media.command('toggle'), false);
});

test('off Windows nothing is spawned and nothing is reported', () => {
  const { spawned, spawn } = fakeSpawn();
  const media = createMedia({ platform: 'darwin', spawn });
  media.start();
  assert.equal(spawned.length, 0);
  assert.equal(media.current(), null);
  assert.equal(media.command('toggle'), false);
  media.stop();
});
