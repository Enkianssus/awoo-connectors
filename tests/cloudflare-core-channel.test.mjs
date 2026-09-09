import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { after, beforeEach, test } from 'node:test';

const source = await readFile(new URL('../cloudflare/appdownload/worker.js', import.meta.url), 'utf8');
const worker = (await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`)).default;
const originalFetch = globalThis.fetch;
const originalCaches = globalThis.caches;
const cache = new Map();
let releases;
let apiStatus;
let atomTags;
let calls;

const stable = tag => ({ tag_name: tag, draft: false, prerelease: false });
const assetCalls = () => calls.filter(call => new URL(call.target).pathname.includes('/releases/download/'));
const request = (route, init) => worker.fetch(new Request(`https://app.enkianss.us${route}`, init), {});
const assertAsset = (tag, asset) => assert.equal(new URL(assetCalls().at(-1).target).pathname, `/Enkianssus/AwooMusicBot/releases/download/${tag}/${asset}`);

globalThis.caches = { default: {
  async match(key) { return cache.get(key.url)?.clone(); },
  async put(key, response) { cache.set(key.url, response.clone()); }
} };
globalThis.fetch = async (input, init = {}) => {
  const target = String(input);
  const url = new URL(target);
  calls.push({ target, init });
  if (url.hostname === 'api.github.com' && url.pathname === '/repos/Enkianssus/AwooMusicBot/releases') {
    return new Response(JSON.stringify(releases), { status: apiStatus, headers: { 'Content-Type': 'application/json' } });
  }
  if (url.hostname === 'github.com' && url.pathname === '/Enkianssus/AwooMusicBot/releases.atom') {
    return new Response(`<feed>${atomTags.map(tag => `<entry><link href="https://github.com/Enkianssus/AwooMusicBot/releases/tag/${tag}"/></entry>`).join('')}</feed>`);
  }
  if (url.hostname === 'github.com' && url.pathname.startsWith('/Enkianssus/AwooMusicBot/releases/download/')) {
    const range = new Headers(init.headers).get('Range');
    const headers = { 'Content-Type': 'application/octet-stream', 'Content-Length': range ? '1' : '8' };
    if (range) headers['Content-Range'] = 'bytes 0-0/8';
    return new Response(init.method === 'HEAD' ? null : range ? 'z' : 'zipbytes', { status: range ? 206 : 200, headers });
  }
  throw new Error(`Unexpected upstream request: ${target}`);
};

beforeEach(() => {
  cache.clear();
  calls = [];
  releases = [stable('v1.1.13'), stable('v1.2.1'), stable('v1.0.9')];
  apiStatus = 200;
  atomTags = [];
});
after(() => { globalThis.fetch = originalFetch; globalThis.caches = originalCaches; });

test('Awoo selects 1.2 stable releases without accepting other series or prereleases', async () => {
  releases = [
    stable('v1.0.999'), stable('v1.1.99'), stable('v1.2.1'),
    stable('v1.3.99'), stable('v2.0.0'), stable('v1.2.99-rc.1'), stable('v1.2.1.9'),
    { ...stable('v1.2.88'), draft: true }, { ...stable('v1.2.77'), prerelease: true }
  ];
  assert.equal((await request('/download/awoo')).status, 200);
  assertAsset('v1.2.1', 'awoo-musicbot-win-Portable.zip');
});

test('Awoo still serves the latest 1.1 release before a stable 1.2 release exists', async () => {
  releases = [stable('v1.1.9'), stable('v1.1.13'), stable('v1.2.2-beta'), stable('v1.3.0')];
  assert.equal((await request('/update/awoo/RELEASES')).status, 200);
  assertAsset('v1.1.13', 'RELEASES');
});

test('legacy download and update routes remain on 1.0 with a separate cache', async () => {
  assert.equal((await request('/download/bilincm')).status, 200);
  assertAsset('v1.0.9', 'bilincm-win-Portable.zip');
  assert.equal((await request('/update/bilincm/RELEASES')).status, 200);
  assertAsset('v1.0.9', 'RELEASES');
  assert.equal((await request('/update/awoo/RELEASES')).status, 200);
  assertAsset('v1.2.1', 'RELEASES');
  assert.equal(cache.size, 2);
});

test('Awoo fails closed when only legacy or unapproved series are available', async () => {
  releases = ['v1.0.999', 'v1.3.0', 'v2.0.0'].map(stable);
  atomTags = releases.map(release => release.tag_name);
  assert.equal((await request('/download/awoo')).status, 502);
  assert.equal(assetCalls().length, 0);
});

test('Atom fallback uses the same explicit stable series boundaries', async () => {
  apiStatus = 403;
  atomTags = ['v1.0.999', 'v1.1.99', 'v1.2.1', 'v1.3.9', 'v2.0.0', 'v1.2.9-rc.1', 'v1.2.1.7'];
  assert.equal((await request('/download/awoo')).status, 200);
  assertAsset('v1.2.1', 'awoo-musicbot-win-Portable.zip');
});

test('expanded Awoo series do not reuse the old 1.1-only cache key', async () => {
  cache.set('https://release-channel-cache.invalid/Enkianssus/AwooMusicBot/1.1.', new Response('v1.1.12'));
  await request('/download/awoo');
  assertAsset('v1.2.1', 'awoo-musicbot-win-Portable.zip');
  await request('/update/awoo/RELEASES');
  assertAsset('v1.2.1', 'RELEASES');
  assert.equal(calls.filter(call => new URL(call.target).hostname === 'api.github.com').length, 1);
  assert.ok(cache.has('https://release-channel-cache.invalid/Enkianssus/AwooMusicBot/1.1.,1.2.'));
});

test('the stable download URL still forwards byte ranges for 1.2 assets', async () => {
  const response = await request('/download/awoo', { headers: { Range: 'bytes=0-0' } });
  assert.equal(response.status, 206);
  assert.equal(response.headers.get('Content-Range'), 'bytes 0-0/8');
  assert.equal(await response.text(), 'z');
  assert.equal(new Headers(assetCalls().at(-1).init.headers).get('Range'), 'bytes=0-0');
  assertAsset('v1.2.1', 'awoo-musicbot-win-Portable.zip');
});

test('all core feed filenames and NUPKG redirect resolve through the expanded channel', async () => {
  for (const asset of ['RELEASES', 'assets.win.json', 'releases.win.json']) {
    assert.equal((await request(`/update/awoo/${asset}`)).status, 200);
    assertAsset('v1.2.1', asset);
  }
  const response = await request('/update/awoo/awoo-musicbot-1.2.1-full.nupkg');
  assert.equal(response.status, 302);
  assert.equal(response.headers.get('Location'), 'https://github.com/Enkianssus/AwooMusicBot/releases/download/v1.2.1/awoo-musicbot-1.2.1-full.nupkg');
});
