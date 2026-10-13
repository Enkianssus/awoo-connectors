import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  profileReleaseMetadata, validateProfileDocument, validateProfilePack, updateProfileCatalog
} from '../scripts/update-profile-catalog.mjs';

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const catalogPath = path.join(repository, 'qqmusic-profile-catalog.json');
const frozenPaths = ['catalog.json', 'qqmusic-profile-catalog.json', 'keys/release-public-key.pem']
  .map(name => path.join(repository, name));
const frozenBytes = new Map(frozenPaths.map(filename => [filename, fs.readFileSync(filename)]));
const original = JSON.parse(fs.readFileSync(catalogPath, 'utf8'));
const legacy = structuredClone(original.profiles.qqmusic);
assert.equal(original.schemaVersion, 1);
assert.equal(original.publicKeyId, 'bilincm-connectors-2026-01');
assert.equal(legacy.version, '1.3.0', 'the old-core entry stays on its immutable schema 1 release');
assert.equal(legacy.schemaVersion, 1);
assert.equal(legacy.minimumConnectorVersion, '22.51.1');

for (const version of ['1.2.1', '1.3.0']) assert.deepEqual(profileReleaseMetadata(version), {
  schemaVersion: 1, catalogKey: 'qqmusic', minimumConnectorVersion: '22.51.1'
});
for (const version of ['1.4.0', '1.4.1', '1.5.0']) assert.deepEqual(profileReleaseMetadata(version), {
  schemaVersion: 2, catalogKey: 'qqmusicV2', minimumConnectorVersion: '22.76.1'
});
for (const version of ['1.4', '1.4.0-beta', '1.04.0', 'one']) assert.throws(() => profileReleaseMetadata(version));

const args = version => [
  version, `awoo-qqmusic-profiles-${version}.zip`, 'a'.repeat(64), Buffer.alloc(64, 17).toString('base64'), '4096',
  `bilincm-qqmusic-profiles-${version}.zip`, 'a'.repeat(64), Buffer.alloc(64, 17).toString('base64'), '4096'
];
const updated = updateProfileCatalog(original, args('1.4.0'));
assert.deepEqual(original.profiles.qqmusic, legacy, 'generator must not mutate its input');
assert.deepEqual(updated.profiles.qqmusic, legacy, 'schema 2 publication must preserve the legacy entry exactly');
assert.equal(updated.schemaVersion, 1);
assert.equal(updated.publicKeyId, original.publicKeyId);
assert.equal(updated.repository, original.repository);
const next = updated.profiles.qqmusicV2;
assert.equal(next.id, 'qqmusic');
assert.equal(next.schemaVersion, 2);
assert.equal(next.minimumConnectorVersion, '22.76.1');
assert.equal(next.version, '1.4.0');
for (const [entry, prefix] of [[next, 'bilincm'], [next.awooPackage, 'awoo']]) {
  const asset = `${prefix}-qqmusic-profiles-1.4.0.zip`;
  assert.equal(entry.asset, asset);
  assert.equal(entry.size, 4096);
  assert.equal(entry.sha256, 'a'.repeat(64));
  assert.equal(entry.signature, Buffer.alloc(64, 17).toString('base64'));
  assert.equal(entry.downloadUrl, `https://app.enkianss.us/connectors/v1/profiles/qqmusic/download/1.4.0/${asset}`);
}
const future = updateProfileCatalog(updated, args('1.4.1'));
assert.deepEqual(future.profiles.qqmusic, legacy);
assert.equal(future.profiles.qqmusicV2.version, '1.4.1');
const oldGeneration = updateProfileCatalog({ ...original, profiles: {} }, args('1.3.0'));
assert.equal(oldGeneration.profiles.qqmusic.schemaVersion, 1);
assert.equal(oldGeneration.profiles.qqmusic.minimumConnectorVersion, '22.51.1');
assert.equal(Object.hasOwn(oldGeneration.profiles, 'qqmusicV2'), false);

for (const changes of [{ schemaVersion: 2 }, { publicKeyId: 'other-key' }, { repository: 'Other/project' }]) {
  assert.throws(() => updateProfileCatalog({ ...original, ...changes }, args('1.4.0')));
}
assert.throws(() => updateProfileCatalog({ ...original, profiles: {} }, args('1.4.0')), /frozen legacy/);
for (const [index, value] of [[1, '../bad.zip'], [2, 'bad-hash'], [3, 'AAAA'], [4, '0'],
  [5, 'awoo-qqmusic-profiles-1.4.0.zip'], [8, '-1']]) {
  const invalid = args('1.4.0'); invalid[index] = value;
  assert.throws(() => updateProfileCatalog(original, invalid));
}

const packDirectory = path.join(repository, 'profiles', 'qqmusic');
const result = validateProfilePack(packDirectory, 2);
for (const version of ['22.22', '22.41', '22.51', '22.52', '22.60', '22.61', '22.71', '22.76']) {
  assert.ok(result.fileVersions.includes(version), `full profile pack retains ${version}`);
}
const profile = JSON.parse(fs.readFileSync(path.join(packDirectory, '22.76.json'), 'utf8'));
assert.equal(profile.schemaVersion, 2);
validateProfileDocument(profile, '22.76.json');
const compatibleFuture = { ...profile, fileVersion: '22.77' };
validateProfileDocument(compatibleFuture, '22.77.json');
for (const changes of [
  { schemaVersion: 3 }, { abiContract: 'unknown-future-abi' }, { transportContract: 'type4-songid-v2' },
  { apiSha256: '' }, { stringManagerGetterRva: '0x0' }, { songItemSize: '0xB0' },
  { expectedPlayDispatchBytes: 'E9 00 00 00 00' }, { arbitraryInstructions: '90 C3' },
  { expectedPlayDispatchBytes: 'E8 FF FF FF 7F' }, { clientSha256: 'wildcard' }
]) assert.throws(() => validateProfileDocument({ ...profile, ...changes }, '22.76.json'));
assert.throws(() => validateProfileDocument(profile, '22.77.json'));
assert.throws(() => validateProfilePack(packDirectory, 1), /frozen legacy/);
assert.throws(() => validateProfilePack(packDirectory, 3), /Unsupported/);

const workflow = fs.readFileSync(path.join(repository, '.github/workflows/release-qqmusic-profiles.yml'), 'utf8');
assert.match(workflow, /--metadata \$version/);
assert.match(workflow, /--validate-pack profiles\/qqmusic/);
assert.match(workflow, /tests\/profile-catalog-policy\.test\.mjs/);
assert.match(workflow, /minimum_connector_version=/);
assert.match(workflow, /schema_version=/);
assert.match(workflow, /github\.ref_name != 'qqmusic-profiles-v1\.4\.0'/);
assert.match(workflow, /awoo-qqmusic-profiles-\$version\.zip/);
assert.match(workflow, /bilincm-qqmusic-profiles-\$version\.zip/);
assert.match(workflow, /git add qqmusic-profile-catalog\.json/);
assert.doesNotMatch(workflow, /git add catalog\.json/);

const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'qq-profile-catalog-policy-'));
try {
  const isolatedPack = path.join(directory, 'pack');
  fs.mkdirSync(isolatedPack);
  const isolatedProfile = path.join(isolatedPack, '22.76.json');
  fs.writeFileSync(isolatedProfile, JSON.stringify(profile));
  assert.equal(validateProfilePack(isolatedPack, 2).count, 1);
  fs.writeFileSync(isolatedProfile, JSON.stringify(profile).replace('"abiContract":',
    '"abiContract":"unknown-abi","abiContract":'));
  assert.throws(() => validateProfilePack(isolatedPack, 2), /Duplicate profile/);
  fs.writeFileSync(isolatedProfile, '{invalid-json');
  assert.throws(() => validateProfilePack(isolatedPack, 2), SyntaxError);
  const filename = path.join(directory, 'qqmusic-profile-catalog.json');
  fs.writeFileSync(filename, JSON.stringify(original));
  const generated = spawnSync(process.execPath, [path.join(repository, 'scripts/update-profile-catalog.mjs'), ...args('1.4.0')],
    { cwd: directory, encoding: 'utf8' });
  assert.equal(generated.status, 0, generated.stderr || generated.stdout);
  const catalog = JSON.parse(fs.readFileSync(filename, 'utf8'));
  assert.deepEqual(catalog.profiles.qqmusic, legacy);
  assert.equal(catalog.profiles.qqmusicV2.version, '1.4.0');
  const beforeInvalid = fs.readFileSync(filename);
  const failed = spawnSync(process.execPath, [path.join(repository, 'scripts/update-profile-catalog.mjs'), ...args('1.4.0').with(2, 'bad')],
    { cwd: directory, encoding: 'utf8' });
  assert.notEqual(failed.status, 0);
  assert.deepEqual(fs.readFileSync(filename), beforeInvalid, 'validation failure must leave the catalog intact');
} finally {
  fs.rmSync(directory, { recursive: true, force: true });
}
for (const [filename, bytes] of frozenBytes) assert.deepEqual(fs.readFileSync(filename), bytes,
  'tests and generators must leave source catalogs and signing key untouched');
console.log('profile-catalog-policy.test.mjs passed.');
