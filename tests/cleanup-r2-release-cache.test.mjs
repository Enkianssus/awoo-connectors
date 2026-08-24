import assert from 'node:assert/strict';
import {
  cleanupR2ReleaseCache,
  selectKeysToDelete
} from '../scripts/cleanup-r2-release-cache.mjs';

const accountId = 'a'.repeat(32);
const awooPrefix =
  'github-release-v1/github.com/Enkianssus/AwooMusicBot/releases/download/';
const currentAwooKey = `${awooPrefix}v1.1.6/awoo-musicbot-win-Portable.zip`;
const awooScope = {
  accountId,
  bucket: 'awoo-download-cache',
  repository: 'Enkianssus/AwooMusicBot',
  currentTag: 'v1.1.6',
  tagPrefix: 'v',
  versionParts: 3,
  expectedKeys: [currentAwooKey]
};

const keys = [
  currentAwooKey,
  `${awooPrefix}v1.1.5/awoo-musicbot-win-Portable.zip`,
  `${awooPrefix}v1.1.5/RELEASES`,
  `${awooPrefix}v1.0.9/awoo-musicbot-win-Portable.zip`,
  `${awooPrefix}v1.1.preview/awoo-musicbot-win-Portable.zip`,
  `${awooPrefix}v1.1.5/nested/unknown.bin`,
  `${awooPrefix}v1.1.5/../unknown.bin`,
  'github-release-v1/github.com/Enkianssus/awoo-connectors/releases/download/'
    + 'kugou-v20.1.41.1/other.zip',
  'unknown-prefix/something'
];
assert.deepEqual(
  selectKeysToDelete(awooScope, keys),
  [
    `${awooPrefix}v1.0.9/awoo-musicbot-win-Portable.zip`,
    `${awooPrefix}v1.1.5/RELEASES`,
    `${awooPrefix}v1.1.5/awoo-musicbot-win-Portable.zip`
  ]
);

const newerAwooKey = `${awooPrefix}v1.1.7/awoo-musicbot-win-Portable.zip`;
assert.deepEqual(
  selectKeysToDelete(awooScope, [
    currentAwooKey,
    newerAwooKey,
    `${awooPrefix}v1.1.5/awoo-musicbot-win-Portable.zip`
  ]),
  [
    `${awooPrefix}v1.1.5/awoo-musicbot-win-Portable.zip`
  ],
  'a higher release keeps both the current and newer tag safe'
);

const connectorPrefix =
  'github-release-v1/github.com/Enkianssus/awoo-connectors/releases/download/';
const connectorCurrent = `${connectorPrefix}netease-v3.1.38.205386.1/asset.zip`;
assert.deepEqual(
  selectKeysToDelete(
    {
      accountId,
      bucket: 'awoo-download-cache',
      repository: 'Enkianssus/awoo-connectors',
      currentTag: 'netease-v3.1.38.205386.1',
      tagPrefix: 'netease-v',
      versionParts: 5,
      expectedKeys: [connectorCurrent]
    },
    [
      connectorCurrent,
      `${connectorPrefix}netease-v3.1.38.205386.0/asset.zip`,
      `${connectorPrefix}netease-v3.1.37.205386.9/asset.zip`,
      `${connectorPrefix}kugou-v20.1.41.1/asset.zip`
    ]
  ),
  [
    `${connectorPrefix}netease-v3.1.37.205386.9/asset.zip`,
    `${connectorPrefix}netease-v3.1.38.205386.0/asset.zip`
  ]
);

function response(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'Content-Type': 'application/json' }
  });
}

const listBody = keys => ({
  success: true,
  result: keys.map(key => ({ key })),
  result_info: { is_truncated: false }
});

let deleteCalls = [];
const listUrls = [];
const successfulFetch = async (input, init = {}) => {
  if (init.method === 'DELETE') {
    deleteCalls.push(String(input));
    return response({ success: true, result: { key: String(input) } });
  }
  listUrls.push(String(input));
  return response(listBody([
    currentAwooKey,
    `${awooPrefix}v1.1.5/awoo-musicbot-win-Portable.zip`
  ]));
};
const cleanupResult = await cleanupR2ReleaseCache(
  awooScope,
  'test-token',
  successfulFetch
);
assert.deepEqual(cleanupResult.deleted, [
  `${awooPrefix}v1.1.5/awoo-musicbot-win-Portable.zip`
]);
assert.equal(deleteCalls.length, 1);
assert.equal(new URL(listUrls[0]).searchParams.get('per_page'), '1000');
assert.equal(new URL(listUrls[0]).searchParams.get('limit'), null);

deleteCalls = [];
const dryRunResult = await cleanupR2ReleaseCache(
  { ...awooScope, dryRun: true },
  'test-token',
  successfulFetch
);
assert.ok(dryRunResult.candidates.length > 0);
assert.deepEqual(dryRunResult.deleted, []);
assert.equal(deleteCalls.length, 0, 'dry-run must never call DELETE');

deleteCalls = [];
await assert.rejects(
  cleanupR2ReleaseCache(
    awooScope,
    'test-token',
    async (input, init = {}) => {
      if (init.method === 'DELETE') {
        deleteCalls.push(String(input));
      }
      return response(listBody([
        `${awooPrefix}v1.1.5/awoo-musicbot-win-Portable.zip`
      ]));
    }
  ),
  /uploaded assets are missing/
);
assert.equal(deleteCalls.length, 0, 'missing uploads must prevent cleanup');

deleteCalls = [];
await assert.rejects(
  cleanupR2ReleaseCache(
    awooScope,
    'test-token',
    async (input, init = {}) => {
      if (init.method === 'DELETE') {
        deleteCalls.push(String(input));
        return response(
          { success: false, errors: [{ message: 'simulated delete failure' }] },
          500
        );
      }
      return response(listBody([
        currentAwooKey,
        `${awooPrefix}v1.1.5/awoo-musicbot-win-Portable.zip`
      ]));
    }
  ),
  /R2 cleanup failed for 1 object/
);
assert.equal(deleteCalls.length, 1, 'delete failure should be visible and attempted');

console.log('cleanup-r2-release-cache.test.mjs passed.');
