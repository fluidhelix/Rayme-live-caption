'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const { PassThrough } = require('node:stream');
const { Capture } = require('../windows-captions/capture');
const { SourceManager, SourceScheduler } = require('../windows-captions/sources');
const { createUI } = require('../windows-captions/server');

function workers() {
  const children = [], timers = new Map();
  let next = 0;
  return {
    children, timers,
    options: { setTimer(fn, delay) { timers.set(++next, { fn, delay }); return next; }, clearTimer(id) { timers.delete(id); } },
    spawn(_exe, args) {
      assert.ok(children.every(child => child.exitCode !== null), 'capture workers must not overlap');
      const child = new EventEmitter();
      child.args = args;
      child.stdout = new PassThrough();
      child.stderr = new PassThrough();
      child.stdin = new PassThrough();
      child.exitCode = null;
      child.exit = code => { child.exitCode = code; child.emit('exit', code); };
      child.kill = () => assert.fail('the fake worker should shut down through stdin');
      child.stdin.on('finish', () => child.exit(0));
      children.push(child);
      return child;
    },
    retry(expectedDelay) {
      assert.equal(timers.size, 1);
      const [id, { fn, delay }] = timers.entries().next().value;
      assert.equal(delay, expectedDelay);
      timers.delete(id);
      fn();
    },
  };
}
const tone = (ms, value) => {
  const pcm = Buffer.alloc(ms * 32);
  for (let i = 0; i < pcm.length; i += 2) pcm.writeInt16LE(value, i);
  return pcm;
};
const tick = () => new Promise(resolve => setImmediate(resolve));
const defaultDevice = { id: 'default', endpoint: 'default', kind: 'device', name: 'Windows default playback' };
const makeCapture = (fixture, audio, status, reset) => new Capture('helper', audio, status, reset, fixture.spawn, fixture.options);

test('default transitions retry once, ignore the old helper and back off when activation fails', async () => {
  const fixture = workers(), events = [];
  const capture = makeCapture(fixture, data => events.push(data.toString()), (message, state) => events.push(state), discard => events.push(discard));
  try {
    await capture.select('default');
    const first = fixture.children[0];
    assert.deepEqual(first.args, ['--device', 'default']);
    first.exit(5);
    assert.deepEqual(events.slice(-2), [true, 'recovering']);
    first.stdout.write('stale PCM');
    first.stderr.write('{"type":"ready"}\n');
    first.emit('error', Error('stale error'));
    assert.deepEqual(events.slice(-2), [true, 'recovering']);
    fixture.retry(150);
    const second = fixture.children[1];
    second.exit(5);
    fixture.retry(150);
    const third = fixture.children[2];
    third.exit(2);
    assert.equal(events.at(-1), 'unavailable');
    fixture.retry(3000);
    const fourth = fixture.children[3];
    fourth.stderr.write('{"type":"ready"}\n');
    fourth.stdout.write('current PCM');
    assert.deepEqual(events.slice(-2), ['capturing', 'current PCM']);
    assert.ok(fixture.children.every(c => c.args[1] === 'default'));
  } finally { await capture.close(); }
  assert.equal(fixture.timers.size, 0);
});

test('explicit endpoints stay pinned and process capture remains terminal on exit', async () => {
  const fixture = workers(), resets = [];
  const capture = makeCapture(fixture, () => {}, () => {}, discard => resets.push(discard));
  try {
    await capture.select('pinned-id');
    fixture.children[0].exit(5);
    assert.equal(resets.at(-1), false);
    fixture.retry(3000);
    assert.deepEqual(fixture.children[1].args, ['--device', 'pinned-id']);
    await capture.select({ pid: 123, started: '456' });
    assert.deepEqual(fixture.children[2].args, ['--process', '123', '--started', '456']);
    fixture.children[2].exit(5);
    assert.equal(fixture.timers.size, 0);
    assert.equal(resets.at(-1), undefined);
  } finally { await capture.close(); }
});

test('pause during recovery cancels even a queued retry and quit shares the active worker shutdown', async () => {
  const fixture = workers();
  const scheduler = new SourceScheduler(async () => '', () => {}, () => {});
  const manager = new SourceManager({ scheduler, publish: () => {}, createCapture: (...args) => makeCapture(fixture, ...args) });
  try {
    await manager.configure('device', defaultDevice);
    fixture.children[0].exit(5);
    const queued = fixture.timers.values().next().value.fn;
    await manager.pause(true);
    queued();
    assert.equal(fixture.children.length, 1);
    assert.equal(fixture.timers.size, 0);
    await manager.pause(false);
    const current = fixture.children[1];
    let ends = 0;
    current.stdin.on('finish', () => ends++);
    await Promise.all([manager.pause(true), manager.close()]);
    assert.equal(current.exitCode, 0);
    assert.equal(ends, 1);
    assert.equal(fixture.children.length, 2);
    assert.equal(manager.slots.size, 0);
  } finally { await manager.close(); scheduler.close(); }
});

test('default handoff discards partial text and old samples, preserves finals and rejects stale inference', async () => {
  const fixture = workers(), ui = await createUI({ port: 0 }), calls = [];
  const scheduler = new SourceScheduler(pcm => new Promise(resolve => calls.push({ pcm, resolve })), ui.publish, () => {});
  const manager = new SourceManager({ scheduler, publish: ui.publish, createCapture: (...args) => makeCapture(fixture, ...args) });
  try {
    await manager.configure('device', defaultDevice);
    const old = fixture.children[0];
    const source = manager.slots.get('device').source;
    old.stdout.write(Buffer.concat([tone(200, 1000), tone(900, 0)]));
    calls.shift().resolve('old provisional final');
    await tick();
    calls.shift().resolve('old finalized caption');
    await tick();
    old.stdout.write(tone(800, 1000));
    calls.shift().resolve('old visible partial');
    await tick();
    assert.equal(ui.state.partials.device.text, 'old visible partial');
    old.stdout.write(tone(800, 1000));
    assert.equal(calls.length, 1, 'an old inference is in flight at handoff');
    old.stdout.write(Buffer.alloc(1, 0x7f)); // Incomplete old PCM frame must be discarded.
    const oldSegmenter = manager.slots.get('device').segmenter;

    old.exit(5);
    const fresh = manager.slots.get('device');
    assert.ok(fresh.source.generation > source.generation);
    assert.equal(fresh.source.name, source.name);
    assert.equal(fresh.source.identity, source.identity);
    assert.notEqual(fresh.segmenter, oldSegmenter);
    assert.equal(ui.state.partials.device, undefined);
    assert.deepEqual(ui.state.history.map(c => c.text), ['old finalized caption']);

    fixture.retry(150);
    fixture.children[1].stdout.write(tone(800, 2000));
    old.stdout.write(tone(2000, 1000));
    assert.equal(calls.length, 1);
    calls.shift().resolve('late old inference');
    await tick();
    assert.equal(calls.length, 1);
    const result = calls.shift();
    assert.ok(result.pcm.length >= 700 * 32);
    for (let i = 0; i < result.pcm.length; i += 2) assert.equal(result.pcm.readInt16LE(i), 2000);
    result.resolve('new endpoint caption');
    await tick();
    assert.equal(ui.state.partials.device.text, 'new endpoint caption');
    assert.equal(ui.state.partials.device.source.generation, fresh.source.generation);
    assert.deepEqual(ui.state.history.map(c => c.text), ['old finalized caption']);
  } finally { await manager.close(); scheduler.close(); await ui.close(); }
});
