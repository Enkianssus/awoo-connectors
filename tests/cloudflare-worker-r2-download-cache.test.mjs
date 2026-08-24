import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repositoryDirectory = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '..'
);
const workerSourcePath = path.join(
  repositoryDirectory,
  'cloudflare',
  'appdownload',
  'worker.js'
);
const temporaryDirectory = fs.mkdtempSync(
  path.join(os.tmpdir(), 'awoo-appdownload-r2-worker-')
);
const temporaryWorkerPath = path.join(temporaryDirectory, 'worker.mjs');
fs.copyFileSync(workerSourcePath, temporaryWorkerPath);
const workerSource = fs.readFileSync(workerSourcePath, 'utf8');
const cacheReadMarker = workerSource.indexOf(
  'const cachedResponse = await readDownloadCache('
);
const redirectMarker = workerSource.indexOf(
  'if (options.redirectReleaseAsset === true)'
);
assert.ok(cacheReadMarker >= 0, 'R2 read path must exist');
assert.ok(
  cacheReadMarker < redirectMarker,
  'redirect assets must try the prewarmed R2 object before returning 302'
);

class MemoryR2Bucket {
  constructor() {
    this.objects = new Map();
    this.putCount = 0;
    this.getCount = 0;
    this.headCount = 0;
  }

  async put(key, value, options = {}) {
    const bytes = new Uint8Array(await new Response(value).arrayBuffer());
    this.objects.set(key, {
      bytes,
      httpMetadata: { ...(options.httpMetadata || {}) },
      customMetadata: { ...(options.customMetadata || {}) },
      httpEtag: 'r2-etag',
      uploaded: new Date('2026-08-01T00:00:00Z')
    });
    this.putCount += 1;
  }

  async head(key) {
    this.headCount += 1;
    const object = this.objects.get(key);
    return object ? this.toObject(object, false, object.bytes.byteLength) : null;
  }

  async get(key, options = {}) {
    this.getCount += 1;
    const object = this.objects.get(key);
    if (!object) return null;
    let bytes = object.bytes;
    if (options.range) {
      const offset = options.range.offset;
      bytes = bytes.slice(offset, offset + options.range.length);
    }
    return this.toObject({ ...object, bytes }, true, object.bytes.byteLength);
  }

  toObject(object, withBody, size) {
    const result = {
      size,
      httpEtag: object.httpEtag,
      uploaded: object.uploaded,
      httpMetadata: { ...object.httpMetadata },
      customMetadata: { ...object.customMetadata }
    };
    if (withBody) {
      result.body = new Response(object.bytes).body;
    }
    return result;
  }
}

function makeExecutionContext() {
  const pending = [];
  return {
    pending,
    waitUntil(promise) {
      pending.push(Promise.resolve(promise));
    }
  };
}

const originalFetch = globalThis.fetch;
const originalCaches = globalThis.caches;
globalThis.caches = {
  default: {
    async match() {
      return null;
    },
    async put() {}
  }
};
const upstreamCalls = [];
globalThis.fetch = async (input, init = {}) => {
  const target = String(input);
  const targetUrl = new URL(target);
  upstreamCalls.push({ target, init });
  if (
    targetUrl.hostname === 'api.github.com'
    && targetUrl.pathname === '/repos/Enkianssus/AwooMusicBot/releases'
  ) {
    return new Response(
      JSON.stringify([{ tag_name: 'v1.1.6', draft: false, prerelease: false }]),
      { status: 200, headers: { 'Content-Type': 'application/json' } }
    );
  }
  if (
    targetUrl.hostname !== 'github.com'
    || !targetUrl.pathname.includes('/releases/download/')
  ) {
    throw new Error(`Unexpected upstream request in test: ${target}`);
  }

  if (targetUrl.pathname.includes('netease-v3.1.38.205386.1')) {
    return new Response(null, {
      status: 500,
      headers: { 'Cache-Control': 'private, no-store' }
    });
  }

  const requestHeaders = new Headers(init.headers);
  const range = requestHeaders.get('Range');
  const body = range ? 'pbyt' : 'zipbytes';
  const status = range ? 206 : 200;
  const headers = {
    'Content-Type': 'application/zip',
    'Content-Length': String(body.length),
    ETag: 'origin-v1',
    'Last-Modified': 'Sat, 01 Aug 2026 00:00:00 GMT'
  };
  if (range) headers['Content-Range'] = 'bytes 2-5/8';
  return new Response(init.method === 'HEAD' ? null : body, {
    status,
    headers
  });
};

try {
  const moduleUrl = `${pathToFileURL(temporaryWorkerPath).href}?test=r2`;
  const worker = (await import(moduleUrl)).default;
  const v2Asset =
    'awoo-connector-kugou-20.1.42.1-win-x86-framework-dependent.zip';
  const v2Url =
    `https://app.enkianss.us/connectors/v2/download/kugou/20.1.42.1/${v2Asset}`;
  const bucket = new MemoryR2Bucket();
  const firstContext = makeExecutionContext();

  let response = await worker.fetch(
    new Request(v2Url),
    { DOWNLOAD_CACHE: bucket },
    firstContext
  );
  assert.equal(response.status, 200);
  assert.equal(await response.text(), 'zipbytes');
  await Promise.all(firstContext.pending);
  assert.equal(upstreamCalls.length, 1);
  assert.equal(bucket.putCount, 1);
  assert.deepEqual(
    [...bucket.objects.keys()],
    [
      'github-release-v1/github.com/Enkianssus/awoo-connectors/releases/download/'
        + `kugou-v20.1.42.1/${v2Asset}`
    ]
  );

  response = await worker.fetch(
    new Request(v2Url, { headers: { Range: 'bytes=2-5' } }),
    { DOWNLOAD_CACHE: bucket },
    makeExecutionContext()
  );
  assert.equal(response.status, 206);
  assert.equal(await response.text(), 'pbyt');
  assert.equal(response.headers.get('Content-Length'), '4');
  assert.equal(response.headers.get('Content-Range'), 'bytes 2-5/8');
  assert.equal(response.headers.get('ETag'), 'origin-v1');
  assert.equal(response.headers.get('X-Awoo-Download-Cache'), 'HIT');
  assert.equal(upstreamCalls.length, 1);

  response = await worker.fetch(
    new Request(v2Url, { method: 'HEAD' }),
    { DOWNLOAD_CACHE: bucket },
    makeExecutionContext()
  );
  assert.equal(response.status, 200);
  assert.equal(await response.text(), '');
  assert.equal(response.headers.get('Content-Length'), '8');
  assert.equal(response.headers.get('Content-Disposition'), `attachment; filename="${v2Asset}"`);
  assert.equal(upstreamCalls.length, 1);

  response = await worker.fetch(
    new Request(v2Url, { headers: { 'If-None-Match': 'origin-v1' } }),
    { DOWNLOAD_CACHE: bucket },
    makeExecutionContext()
  );
  assert.equal(response.status, 304);
  assert.equal(await response.text(), '');
  assert.equal(upstreamCalls.length, 1);

  const rangeFirstBucket = new MemoryR2Bucket();
  const rangeFirstUrl =
    'https://app.enkianss.us/connectors/v2/download/qqmusic/22.52.1/'
    + 'awoo-connector-qqmusic-22.52.1-win-x86-framework-dependent.zip';
  response = await worker.fetch(
    new Request(rangeFirstUrl, { headers: { Range: 'bytes=2-5' } }),
    { DOWNLOAD_CACHE: rangeFirstBucket },
    makeExecutionContext()
  );
  assert.equal(response.status, 206);
  assert.equal(rangeFirstBucket.putCount, 0);

  const failingBucket = new MemoryR2Bucket();
  const failingUrl =
    'https://app.enkianss.us/connectors/v2/download/netease/3.1.38.205386.1/'
    + 'awoo-connector-netease-3.1.38.205386.1-win-x64-framework-dependent.zip';
  response = await worker.fetch(
    new Request(failingUrl),
    { DOWNLOAD_CACHE: failingBucket },
    makeExecutionContext()
  );
  assert.equal(response.status, 500);
  assert.equal(failingBucket.putCount, 0);
  const callsAfterFirstFailure = upstreamCalls.length;
  await worker.fetch(
    new Request(failingUrl),
    { DOWNLOAD_CACHE: failingBucket },
    makeExecutionContext()
  );
  assert.equal(upstreamCalls.length, callsAfterFirstFailure + 1);

  response = await worker.fetch(new Request(v2Url), {});
  assert.equal(response.status, 200);
  assert.equal(await response.text(), 'zipbytes');
  assert.equal(upstreamCalls.length, callsAfterFirstFailure + 2);

  const nupkgKey =
    'github-release-v1/github.com/Enkianssus/AwooMusicBot/releases/download/'
    + 'v1.1.6/awoo-musicbot-win.nupkg';
  const prewarmedNupkgBucket = new MemoryR2Bucket();
  prewarmedNupkgBucket.objects.set(nupkgKey, {
    bytes: new TextEncoder().encode('nupkg-bytes'),
    httpMetadata: {
      contentType: 'application/octet-stream',
      contentDisposition: 'attachment; filename="awoo-musicbot-win.nupkg"',
      cacheControl: 'public, max-age=31536000, immutable'
    },
    customMetadata: { originEtag: 'nupkg-v1' },
    httpEtag: 'r2-etag',
    uploaded: new Date('2026-08-05T12:34:56Z')
  });
  response = await worker.fetch(
    new Request('https://app.enkianss.us/update/awoo/awoo-musicbot-win.nupkg'),
    { DOWNLOAD_CACHE: prewarmedNupkgBucket },
    makeExecutionContext()
  );
  assert.equal(response.status, 200);
  assert.equal(await response.text(), 'nupkg-bytes');
  assert.equal(response.headers.get('X-Awoo-Download-Cache'), 'HIT');
  assert.equal(
    response.headers.get('Last-Modified'),
    'Wed, 05 Aug 2026 12:34:56 GMT'
  );

  const emptyNupkgBucket = new MemoryR2Bucket();
  response = await worker.fetch(
    new Request('https://app.enkianss.us/update/awoo/awoo-musicbot-win.nupkg'),
    { DOWNLOAD_CACHE: emptyNupkgBucket },
    makeExecutionContext()
  );
  assert.equal(response.status, 302);
  assert.match(
    response.headers.get('Location'),
    /github\.com\/Enkianssus\/AwooMusicBot\/releases\/download\/v1\.1\.6\//
  );

  console.log('cloudflare-worker-r2-download-cache.test.mjs passed.');
} finally {
  globalThis.fetch = originalFetch;
  if (originalCaches === undefined) {
    delete globalThis.caches;
  } else {
    globalThis.caches = originalCaches;
  }
  fs.rmSync(temporaryDirectory, { recursive: true, force: true });
}
