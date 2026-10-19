import test from 'node:test';
import assert from 'node:assert/strict';
import { blockedNetworkGlobals, installNetworkBlocks } from '../../src/SourceVault.Editor.Web/shell.mjs';

// A stand-in for the page global with working network APIs, so removal is observable.
function fakeWindow() {
  const scope = { opened: [], beacons: [] };
  for (const name of blockedNetworkGlobals) scope[name] = class { constructor() { throw new Error('network API reached: ' + name); } };
  scope.open = url => { scope.opened.push(url); return {}; };
  scope.navigator = { sendBeacon: url => { scope.beacons.push(url); return true; }, serviceWorker: { register: () => Promise.resolve() } };
  return scope;
}

test('every blocked network constructor is gone and cannot be put back', () => {
  const scope = fakeWindow();
  installNetworkBlocks(scope);
  for (const name of blockedNetworkGlobals) {
    assert.equal(scope[name], undefined, name);
    assert.throws(() => Object.defineProperty(scope, name, { value: class {} }), TypeError, name + ' redefined');
    assert.throws(() => { 'use strict'; scope[name] = class {}; }, TypeError, name + ' reassigned');
    assert.equal(scope[name], undefined, name + ' after attempts');
  }
});

test('the WebRTC, WebSocket, EventSource and WebTransport entry points are all covered', () => {
  for (const name of ['RTCPeerConnection', 'webkitRTCPeerConnection', 'RTCDataChannel', 'WebSocket', 'WebSocketStream', 'EventSource', 'WebTransport', 'SharedWorker'])
    assert.ok(blockedNetworkGlobals.includes(name), name);
});

test('window.open returns null and opens nothing, even for hostile URLs', () => {
  const scope = fakeWindow();
  installNetworkBlocks(scope);
  for (const url of ['https://example.com/', 'mailto:a@example.com', 'ms-settings:', 'file:///C:/', 'javascript:alert(1)', undefined])
    assert.equal(scope.open(url, '_blank', 'noopener'), null);
  assert.deepEqual(scope.opened, []);
  assert.throws(() => Object.defineProperty(scope, 'open', { value: () => ({}) }), TypeError);
});

test('sendBeacon sends nothing and service workers are unavailable', () => {
  const scope = fakeWindow();
  installNetworkBlocks(scope);
  assert.equal(scope.navigator.sendBeacon('https://example.com/collect', 'data'), false);
  assert.deepEqual(scope.beacons, []);
  assert.equal(scope.navigator.serviceWorker, undefined);
  assert.throws(() => Object.defineProperty(scope.navigator, 'sendBeacon', { value: () => true }), TypeError);
  assert.throws(() => Object.defineProperty(scope.navigator, 'serviceWorker', { value: {} }), TypeError);
});

test('a scope that lacks some APIs (older runtimes, no navigator) still installs cleanly', () => {
  const scope = {};
  installNetworkBlocks(scope);
  assert.equal(scope.WebSocket, undefined);
  assert.equal(scope.open(), null);
});

test('the guard installs over the native host document-created script, which already removed WebRTC', () => {
  const scope = fakeWindow();
  // Mirrors NetworkPolicy.DocumentCreatedScript in the editor host.
  for (const n of ['RTCPeerConnection', 'webkitRTCPeerConnection', 'RTCDataChannel', 'RTCIceTransport'])
    Object.defineProperty(scope, n, { value: undefined, writable: false, configurable: false });
  installNetworkBlocks(scope);
  assert.equal(scope.RTCPeerConnection, undefined);
  assert.equal(scope.WebSocket, undefined);
});

test('installing twice fails loudly rather than silently leaving APIs in place', () => {
  const scope = fakeWindow();
  installNetworkBlocks(scope);
  assert.throws(() => installNetworkBlocks(scope), TypeError);
  assert.equal(scope.WebSocket, undefined);
});
