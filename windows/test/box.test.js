'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const box = require('../src/core/box');

const FIX = path.join(__dirname, 'fixtures', 'crosscheck');
const fixture = (name) => fs.readFileSync(path.join(FIX, name), 'utf8').trim();
const KEY = Buffer.from(fixture('key.txt'), 'base64url'); // the Swift fixtures' key, 32 bytes
const SAMPLE = '{"v":1,"g":"group123456789012","k":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","n":"MacBook Pro"}';

test('base64url: no padding, - and _, strict decoding', () => {
  assert.equal(box.b64url(Buffer.from([0xfb, 0xff])), '-_8');
  assert.deepEqual(box.fromB64url('-_8'), Buffer.from([0xfb, 0xff]));
  assert.deepEqual(box.fromB64url('+/8'), Buffer.from([0xfb, 0xff])); // Swift's decoder takes both alphabets too
  assert.throws(() => box.fromB64url('a*b'));
  assert.throws(() => box.fromB64url('abcde'));
  assert.throws(() => box.fromB64url('ab=='));
});

test('randomGroup: 16-byte g and 32-byte k, unpadded, fresh each time', () => {
  const a = box.randomGroup();
  assert.equal(box.fromB64url(a.g).length, 16);
  assert.equal(box.fromB64url(a.k).length, 32);
  assert.doesNotMatch(a.g + a.k, /=/);
  assert.notEqual(box.randomGroup().g, a.g);
});

test('seal and open round trip; layout is nonce | ciphertext | tag, base64', () => {
  const obj = { a: 1, t: 'Hùng ✓ "q"', arr: [1, 2] };
  const sealed = box.seal(obj, KEY);
  assert.deepEqual(box.open(sealed, KEY), obj);
  assert.equal(Buffer.from(sealed, 'base64').length, 12 + Buffer.byteLength(JSON.stringify(obj)) + 16);
  assert.notEqual(box.seal(obj, KEY), sealed, 'a fresh nonce each time');
});

test('tampered, truncated, wrong-key and malformed boxes do not open', () => {
  const b = Buffer.from(box.seal({ x: 1 }, KEY), 'base64');
  const flip = (i) => { const t = Buffer.from(b); t[i] ^= 1; return t.toString('base64'); };
  assert.throws(() => box.open(flip(12), KEY), 'ciphertext byte');
  assert.throws(() => box.open(flip(b.length - 1), KEY), 'tag byte');
  assert.throws(() => box.open(flip(0), KEY), 'nonce byte');
  assert.throws(() => box.open(box.seal({ x: 1 }, KEY), Buffer.alloc(32, 9)), 'wrong key');
  assert.throws(() => box.open(b.subarray(0, 20).toString('base64'), KEY), 'too short');
  assert.throws(() => box.open('@@@@', KEY), 'not base64');
  assert.throws(() => box.seal({}, Buffer.alloc(16)), 'key length');
});

test('sealBytes / openBytes round trip raw buffers', () => {
  const raw = Buffer.from([0, 1, 2, 255]);
  assert.deepEqual(box.openBytes(box.sealBytes(raw, KEY), KEY), raw);
});

test('frame: 4-byte big-endian length then the bytes', () => {
  const f = box.frame(Buffer.alloc(300, 7));
  assert.equal(f.length, 304);
  assert.equal(f.readUInt32BE(0), 300);
  assert.throws(() => box.frame(Buffer.alloc(0)));
  assert.throws(() => box.frame(Buffer.alloc(box.MAX_FRAME + 1)));
});

test('FrameReader: split and merged chunks, and a sealed box through a frame', () => {
  const bodies = [Buffer.alloc(300, 7), Buffer.from('hello')];
  const stream = Buffer.concat(bodies.map(box.frame));
  const bytewise = new box.FrameReader();
  const got = [];
  for (const byte of stream) got.push(...bytewise.push(Buffer.from([byte])));
  assert.deepEqual(got, bodies);
  assert.deepEqual(new box.FrameReader().push(stream), bodies);
  const r = new box.FrameReader();
  assert.deepEqual(r.push(stream.subarray(0, 2)), []);
  assert.deepEqual(r.push(stream.subarray(2)), bodies);
  const sealed = Buffer.from(box.seal({ ok: true }, KEY), 'base64');
  const [payload] = new box.FrameReader().push(box.frame(sealed));
  assert.deepEqual(box.open(payload.toString('base64'), KEY), { ok: true });
});

test('FrameReader drops a zero length or one over 4 MB by throwing', () => {
  const head = (n) => { const h = Buffer.alloc(4); h.writeUInt32BE(n, 0); return h; };
  assert.throws(() => new box.FrameReader().push(head(0)), RangeError);
  assert.throws(() => new box.FrameReader().push(head(4 * 1024 * 1024 + 1)), RangeError);
  assert.deepEqual(new box.FrameReader().push(head(4 * 1024 * 1024)), [], 'exactly 4 MB waits for its body');
});

test('parsePairing: every case of Pairing.selfTest (iOS/Shared/Seal.swift)', () => {
  const frag = encodeURIComponent(SAMPLE);
  const want = { v: 1, g: 'group123456789012', k: 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA', n: 'MacBook Pro' };
  assert.deepEqual(box.parsePairing(SAMPLE), want, 'bare text');
  assert.deepEqual(box.parsePairing('https://notchtasks.web.app/pair#' + frag), want, 'https URL');
  assert.deepEqual(box.parsePairing('notchtasks://pair#' + frag), want, 'notchtasks:// URL');
  assert.equal(box.parsePairing('https://example.com/pair#' + frag), null, 'foreign URL');
  assert.equal(box.parsePairing('otherapp://pair#' + frag), null, 'foreign scheme');
  assert.equal(box.parsePairing('notchtasks://pair#'), null, 'empty fragment');
  assert.equal(box.parsePairing('https://notchtasks.web.app/pair#'), null, 'empty fragment https');
});

test('parsePairing: smart quotes, whitespace, and the rules on v, g, k and n', () => {
  const want = { v: 1, g: 'group123456789012', k: 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA', n: 'MacBook Pro' };
  assert.deepEqual(box.parsePairing('  ' + SAMPLE.replace(/"/g, '\u201C') + '\n'), want, 'typed smart quotes');
  const with_ = (o) => JSON.stringify({ v: 1, g: 'group123456789012', k: want.k, n: 'x', ...o });
  assert.equal(box.parsePairing(with_({ v: 2 })), null, 'v is 1');
  assert.equal(box.parsePairing(with_({ v: '1' })), null, 'v is a number');
  assert.equal(box.parsePairing(with_({ g: 'g'.repeat(15) })), null, 'g of 15 characters');
  assert.equal(box.parsePairing(with_({ g: 'group 123456789012' })), null, 'g with a space');
  assert.equal(box.parsePairing(with_({ g: 'nhómHùng123456789' })).g, 'nhómHùng123456789', 'g may hold letters (Swift isLetter)');
  assert.equal(box.parsePairing(with_({ k: 'A'.repeat(42) })), null, 'k of 31 bytes');
  assert.equal(box.parsePairing(with_({ k: want.k + '=' })), null, 'k with padding');
  assert.equal(box.parsePairing(with_({ k: want.k.slice(0, -1) + '*' })), null, 'k with a bad character');
  const noN = JSON.parse(with_({})); delete noN.n;
  assert.equal(box.parsePairing(JSON.stringify(noN)), null, 'n is required');
  for (const junk of ['', 'hello', 'a://b', '[]', '{', 'null']) assert.equal(box.parsePairing(junk), null, `junk ${junk}`);
});

test('parsePairing reads the Swift fixtures pair.txt and pair-url.txt', () => {
  const want = { v: 1, g: 'group123456789012', k: fixture('key.txt'), n: "Hùng's Mac" };
  assert.deepEqual(box.parsePairing(fixture('pair.txt')), want);
  assert.deepEqual(box.parsePairing(fixture('pair-url.txt')), want);
});

test('pairCode and pairURL reproduce the Swift fixtures exactly', () => {
  const p = box.parsePairing(fixture('pair.txt'));
  assert.equal(box.pairCode({ g: p.g, k: p.k, name: p.n }), fixture('pair.txt'));
  assert.equal(box.pairURL(fixture('pair.txt')), fixture('pair-url.txt'));
  assert.equal(box.pairCode({ g: 'g', k: 'k', name: 'Mac "Pro"' }), '{"v":1,"g":"g","k":"k","n":"Mac \'Pro\'"}');
  const code = box.pairCode({ g: 'group123456789012', k: fixture('key.txt'), name: 'Hùng "Pro" 100%' });
  assert.deepEqual(box.parsePairing(box.pairURL(code)), { v: 1, g: 'group123456789012', k: fixture('key.txt'), n: "Hùng 'Pro' 100%" }, 'a URL round trip');
});

test('serviceName equals the Swift fixture and reveals nothing of the group', () => {
  assert.equal(box.serviceName('group123456789012'), fixture('service.txt'));
  assert.match(box.serviceName('group123456789012'), /^NotchTasks [0-9a-f]{12}$/);
  assert.notEqual(box.serviceName('abc'), box.serviceName('abd'));
});
