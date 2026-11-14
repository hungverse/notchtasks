'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { createTrack } = require('../src/core/track');

function memory(initial = {}) {
  const store = new Map(Object.entries(initial));
  return { load: (k) => store.get(k), save: (k, v) => store.set(k, v), store };
}

function fakeRequest() {
  const calls = [];
  const request = async (url, body) => { calls.push({ url, body: JSON.parse(body) }); return 204; };
  return { calls, request };
}

test('payload shape: client id, events, session, engagement and platform', async () => {
  const mem = memory();
  const { calls, request } = fakeRequest();
  const t = createTrack({ measurementId: 'G-TEST', apiSecret: 'secret', load: mem.load, save: mem.save,
    request, session: 1234, randomId: () => 'cid-1', env: {} });
  t.event('timer_start', { minutes: 25 });
  t.screen('settings_media');
  await t.flush();

  assert.equal(calls.length, 1);
  assert.match(calls[0].url, /^https:\/\/www\.google-analytics\.com\/mp\/collect\?measurement_id=G-TEST&api_secret=secret$/);
  assert.equal(calls[0].body.client_id, 'cid-1');
  assert.deepEqual(calls[0].body.events, [
    { name: 'timer_start', params: { session_id: 1234, engagement_time_msec: 1, platform: 'windows', minutes: 25 } },
    { name: 'screen_view', params: { session_id: 1234, engagement_time_msec: 1, platform: 'windows', screen_name: 'settings_media' } },
  ]);
  assert.equal(mem.store.get('analytics.cid'), 'cid-1');
});

test('the client id is stable across instances', async () => {
  const mem = memory({ 'analytics.cid': 'saved-id' });
  const { calls, request } = fakeRequest();
  const t = createTrack({ measurementId: 'G-TEST', load: mem.load, save: mem.save, request, env: {} });
  t.event('notch_open');
  await t.flush();
  assert.equal(calls[0].body.client_id, 'saved-id');
});

test('empty measurement id: nothing is queued or sent', async () => {
  const { calls, request } = fakeRequest();
  const t = createTrack({ measurementId: '', apiSecret: '', request, env: {} });
  t.event('notch_open');
  t.screen('x');
  await t.flush();
  assert.equal(calls.length, 0);
});

test('opt-out stops events and clears the queue; CI_SELFTEST stops them too', async () => {
  const mem = memory();
  const { calls, request } = fakeRequest();
  const t = createTrack({ measurementId: 'G-TEST', load: mem.load, save: mem.save, request, env: {} });
  t.event('notch_open');
  t.enabled = false;
  t.event('notch_open');
  await t.flush();
  assert.equal(calls.length, 0);
  assert.equal(t.enabled, false);

  t.enabled = true;
  const ci = createTrack({ measurementId: 'G-TEST', request, env: { CI_SELFTEST: '1' } });
  ci.event('notch_open');
  await ci.flush();
  assert.equal(calls.length, 0);
});

test('a failed request is dropped, not retried', async () => {
  const t = createTrack({ measurementId: 'G-TEST', request: async () => { throw new Error('offline'); }, env: {} });
  t.event('notch_open');
  await assert.doesNotReject(t.flush());
});
