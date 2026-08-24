import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const connectorDirectory = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '..'
);
const rootDirectory = path.resolve(connectorDirectory, '..');
const read = filePath => fs.readFileSync(filePath, 'utf8');
const worker = read(path.join(connectorDirectory, 'cloudflare/appdownload/worker.js'));
const workerConfig = read(
  path.join(connectorDirectory, 'cloudflare/appdownload/wrangler.toml')
);
const rootReleasePath = path.join(rootDirectory, '.github/workflows/release.yml');
const rootRelease = fs.existsSync(rootReleasePath) ? read(rootReleasePath) : null;
const rootCleanupPath = path.join(rootDirectory, 'scripts/cleanup-r2-release-cache.mjs');
const rootCleanup = fs.existsSync(rootCleanupPath) ? read(rootCleanupPath) : null;
const connectorRelease = read(
  path.join(connectorDirectory, '.github/workflows/release-connector.yml')
);
const profileRelease = read(
  path.join(connectorDirectory, '.github/workflows/release-qqmusic-profiles.yml')
);
const cleanupScript = read(
  path.join(connectorDirectory, 'scripts/cleanup-r2-release-cache.mjs')
);

const keyFunction = worker.match(
  /function makeDownloadCacheKey\(targetUrl\) \{([\s\S]*?)\n\}/
);
assert.ok(keyFunction, 'Worker must expose a stable R2 key helper');
assert.match(keyFunction[1], /targetUrl\.hostname/);
assert.match(keyFunction[1], /targetUrl\.pathname/);
assert.doesNotMatch(keyFunction[1], /targetUrl\.search/);
assert.match(worker, /X-Awoo-Download-Cache/);
assert.match(worker, /responseCacheControl: 'public, max-age=300'/);
assert.match(worker, /object\.uploaded/);
assert.match(workerConfig, /^\[\[r2_buckets\]\]\s*$/m);
assert.match(workerConfig, /^binding = "DOWNLOAD_CACHE"\s*$/m);
assert.match(workerConfig, /^bucket_name = "awoo-download-cache"\s*$/m);
assert.doesNotMatch(workerConfig, /#\s*(?:binding|bucket_name)\s*=\s*"(?:DOWNLOAD_CACHE|awoo-download-cache)"/);
assert.match(cleanupScript, /per_page/);
assert.doesNotMatch(cleanupScript, /limit:\s*'1000'/);
assert.match(cleanupScript, /versionParts/);
assert.match(cleanupScript, /At least one successfully uploaded expected key/);
assert.match(cleanupScript, /R2 object delete/);
assert.doesNotMatch(cleanupScript, /--api-base/);
assert.ok(rootCleanup, 'root release must have a local R2 cleanup implementation');
assert.match(rootCleanup, /per_page:\s*'1000'/);
assert.match(rootCleanup, /CACHE_BUCKET = 'awoo-download-cache'/);
assert.match(rootCleanup, /CACHE_REPOSITORY = 'Enkianssus\/AwooMusicBot'/);
assert.match(rootCleanup, /missingKeys/);
assert.match(rootCleanup, /dryRun/);
assert.doesNotMatch(rootCleanup, /--api-base/);

function assertPrewarmWorkflow(source, expectedAssetCount, repository) {
  assert.match(source, /Prewarm .* assets in R2/);
  const prewarmStart = source.indexOf('Prewarm');
  const nextStep = source.indexOf('\n      - ', prewarmStart);
  const prewarmBlock = source.slice(
    prewarmStart,
    nextStep < 0 ? source.length : nextStep
  );
  assert.doesNotMatch(
    prewarmBlock,
    /continue-on-error:\s*true/,
    'configured R2 prewarm failures must fail the prewarm step'
  );
  assert.match(source, /secrets\.ENKIANSSUS_CLOUDFLARE_API_TOKEN/);
  assert.match(prewarmBlock, /is required for R2 prewarm/);
  assert.doesNotMatch(prewarmBlock, /skipping R2 prewarm/);
  assert.match(source, /wranglerVersion = '4\.125\.0'/);
  assert.match(source, /\$bucket = 'awoo-download-cache'/);
  assert.match(
    source,
    new RegExp(
      `github-release-v1/github\.com/${repository.replace('/', '\/')}/releases/download/`
    )
  );
  assert.match(source, /'--remote'/);
  assert.match(source, /'--content-type'/);
  assert.match(source, /'--content-disposition'/);
  assert.match(source, /'--cache-control'/);
  assert.match(source, /'--force'/);
  assert.match(prewarmBlock, /--dry-run/);
  assert.match(prewarmBlock, /upload verification failed/);
  if (repository === 'Enkianssus/AwooMusicBot') {
    assert.match(source, /Get-ChildItem -Path 'Releases' -File/);
  } else {
    assert.match(source, /\$assetPaths = @\(/);
    assert.match(source, /steps\.metadata\.outputs\.(framework_asset|asset)/);
    assert.match(source, /steps\.metadata\.outputs\.(framework_asset|legacy_asset)/);
    assert.equal(
      (source.match(/\.sig/g) || []).length >= expectedAssetCount / 3,
      true,
      'prewarm workflow should enumerate signed release assets'
    );
  }
}

function assertCleanupWorkflow(source, updateStepMarker, repository) {
  const cleanupStart = source.indexOf('Clean old');
  assert.ok(cleanupStart >= 0, 'workflow must clean old R2 objects');
  const cleanupBlock = source.slice(cleanupStart);
  assert.match(cleanupBlock, /scripts\/cleanup-r2-release-cache\.mjs|Invoke-RestMethod/);
  assert.match(cleanupBlock, /--expected-key|missingKeys/);
  assert.match(cleanupBlock, /--current-tag|current release tag|releaseTag/);
  assert.match(cleanupBlock, new RegExp(repository.replace('/', '\\/')));
  assert.doesNotMatch(cleanupBlock, /continue-on-error:\s*true/);
  if (updateStepMarker) {
    const updateIndex = source.indexOf(updateStepMarker);
    assert.ok(updateIndex >= 0, `missing catalog step: ${updateStepMarker}`);
    assert.ok(updateIndex < cleanupStart, 'catalog must update before cleanup');
  }
}

if (rootRelease) {
  assertPrewarmWorkflow(rootRelease, 6, 'Enkianssus/AwooMusicBot');
  assert.match(rootRelease, /scripts\/cleanup-r2-release-cache\.mjs/);
  assert.match(rootRelease, /group: app-release-r2/);
}
assertPrewarmWorkflow(connectorRelease, 3, 'Enkianssus/awoo-connectors');
assertPrewarmWorkflow(profileRelease, 6, 'Enkianssus/awoo-connectors');
assertCleanupWorkflow(
  connectorRelease,
  'Update forward v2 catalog',
  'Enkianssus/awoo-connectors'
);
assertCleanupWorkflow(
  profileRelease,
  'Update profile catalog',
  'Enkianssus/awoo-connectors'
);

console.log('cloudflare-r2-workflow-contract.test.mjs passed.');
