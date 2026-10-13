import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const publicKeyId = 'bilincm-connectors-2026-01';
const repository = 'Enkianssus/awoo-connectors';
const baseFields = [
  'schemaVersion', 'fileVersion', 'clientSha256', 'commonSha256',
  'singleSongPlayDispatchRva', 'expectedPlayDispatchBytes', 'getCatManagerRva',
  'getQqUinExRva', 'songItemConstructorRva', 'songItemDestructorRva', 'addSongsRva',
  'hiddenCategoryIdRva', 'getListRootRva', 'getListHelperRva', 'getCategoryCountRva',
  'songItemSize', 'evidence'
];
const contractFields = ['apiSha256', 'abiContract', 'transportContract', 'stringManagerGetterRva'];

function versionParts(version) {
  if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(version)) {
    throw new Error(`Invalid QQ profile version: ${version}`);
  }
  const parts = version.split('.').map(Number);
  if (!parts.every(Number.isSafeInteger)) throw new Error('Profile version is out of range.');
  return parts;
}

export function profileReleaseMetadata(version) {
  const [major, minor] = versionParts(version);
  const schemaVersion = major > 1 || (major === 1 && minor >= 4) ? 2 : 1;
  return {
    schemaVersion,
    catalogKey: schemaVersion === 2 ? 'qqmusicV2' : 'qqmusic',
    minimumConnectorVersion: schemaVersion === 2 ? '22.76.1' : '22.51.1'
  };
}

function parseRva(value, label) {
  if (typeof value !== 'string' || !/^(?:0x[0-9a-f]+|\d+)$/i.test(value)) {
    throw new Error(`Invalid ${label}.`);
  }
  const number = Number(value);
  if (!Number.isSafeInteger(number) || number < 0x1000 || number > 0x0fffffff) {
    throw new Error(`${label} is outside the supported RVA range.`);
  }
  return number;
}

export function validateProfileDocument(document, filename) {
  if (!document || typeof document !== 'object' || Array.isArray(document)
      || ![1, 2].includes(document.schemaVersion)) throw new Error('Unsupported profile schema.');
  const fields = document.schemaVersion === 2 ? [...baseFields, ...contractFields] : baseFields;
  if (Object.keys(document).some(key => !fields.includes(key))
      || fields.some(key => !Object.hasOwn(document, key))) throw new Error('Unexpected or missing profile field.');
  if (!/^\d+\.\d+$/.test(document.fileVersion)
      || path.basename(filename) !== `${document.fileVersion}.json`) throw new Error('Profile file/version mismatch.');
  for (const field of ['clientSha256', 'commonSha256', ...(document.schemaVersion === 2 ? ['apiSha256'] : [])]) {
    if (!/^[0-9a-f]{64}$/i.test(document[field])) throw new Error(`Invalid ${field}.`);
  }
  for (const field of fields.filter(key => key.endsWith('Rva'))) parseRva(document[field], field);
  const bytes = document.expectedPlayDispatchBytes;
  if (typeof bytes !== 'string' || !/^E8(?: [0-9a-f]{2}){4}$/i.test(bytes)) throw new Error('Expected five-byte relative call.');
  const target = parseRva(document.singleSongPlayDispatchRva, 'dispatch') + 5
    + Buffer.from(bytes.replaceAll(' ', ''), 'hex').readInt32LE(1);
  if (target < 0x1000 || target > 0x0fffffff) throw new Error('Dispatch target is outside the supported RVA range.');
  if (!['0xA0', '0xa0', '160'].includes(document.songItemSize)) throw new Error('Unsupported SongItem layout.');
  if (typeof document.evidence !== 'string' || !document.evidence.trim()) throw new Error('Profile evidence is required.');
  if (document.schemaVersion === 2 && (document.abiContract !== 'qqmusic-addsongs-cstring-v1'
      || document.transportContract !== 'type4-songid-v1')) throw new Error('Unsupported native ABI or transport contract.');
  return document;
}

export function validateProfilePack(directory, schemaVersion) {
  if (![1, 2].includes(schemaVersion)) throw new Error('Unsupported profile pack schema.');
  const filenames = fs.readdirSync(directory).filter(name => name.endsWith('.json')).sort();
  if (!filenames.length) throw new Error('Profile pack is empty.');
  const profiles = filenames.map(filename => {
    const text = fs.readFileSync(path.join(directory, filename), 'utf8');
    const document = JSON.parse(text);
    // Match complete JSON string tokens so quoted content cannot become a key.
    // Runtime rejects duplicate top-level keys, including different casing.
    const tokens = text.match(/"(?:\\.|[^"\\])*"|[{}\[\]:,]/g) || [];
    const names = new Set();
    let depth = 0;
    for (let index = 0; index < tokens.length; index++) {
      const token = tokens[index];
      if (token === '{' || token === '[') depth++;
      else if (token === '}' || token === ']') depth--;
      else if (depth === 1 && token.startsWith('"') && tokens[index + 1] === ':') {
        const key = JSON.parse(token).toLowerCase();
        if (names.has(key)) throw new Error('Duplicate profile property.');
        names.add(key);
      }
    }
    return validateProfileDocument(document, filename);
  });
  if (schemaVersion === 1 && profiles.some(profile => profile.schemaVersion !== 1)) {
    throw new Error('Schema 2 documents must not replace the frozen legacy profile pack.');
  }
  if (schemaVersion === 2 && !profiles.some(profile => profile.schemaVersion === 2)) {
    throw new Error('A schema 2 pack must include a supported execution profile.');
  }
  return { schemaVersion, count: profiles.length, fileVersions: profiles.map(profile => profile.fileVersion) };
}

function validateAsset(version, asset, sha256, signature, sizeText, prefix) {
  if (asset !== `${prefix}-qqmusic-profiles-${version}.zip`) throw new Error('Unexpected QQ profile asset name.');
  if (!/^[0-9a-f]{64}$/i.test(sha256)) throw new Error('Profile SHA-256 must contain 64 hexadecimal characters.');
  if (!/^[A-Za-z0-9+/]{86}==$/.test(signature) || Buffer.from(signature, 'base64').length !== 64) {
    throw new Error('Profile signature must be a base64 Ed25519 signature.');
  }
  const size = Number(sizeText);
  if (!Number.isSafeInteger(size) || size <= 0) throw new Error('Profile asset size must be a positive integer.');
  return { asset, size, sha256, signature,
    downloadUrl: `https://app.enkianss.us/connectors/v1/profiles/qqmusic/download/${version}/${asset}` };
}

export function updateProfileCatalog(catalog, args) {
  const [version, awooAsset, awooHash, awooSignature, awooSize,
    legacyAsset, legacyHash, legacySignature, legacySize] = args;
  if (args.length !== 9 || args.some(value => !value)) throw new Error('Expected version and both signed profile assets.');
  const metadata = profileReleaseMetadata(version);
  if (catalog.schemaVersion !== 1 || catalog.repository !== repository || catalog.publicKeyId !== publicKeyId
      || !catalog.profiles || typeof catalog.profiles !== 'object' || Array.isArray(catalog.profiles)) {
    throw new Error('Profile catalog must retain its v1 envelope, repository and signing key.');
  }
  if (metadata.schemaVersion === 2 && (catalog.profiles.qqmusic?.version !== '1.3.0'
      || catalog.profiles.qqmusic?.schemaVersion !== 1)) {
    throw new Error('The frozen legacy QQ profile entry must remain present.');
  }
  const awooPackage = validateAsset(version, awooAsset, awooHash, awooSignature, awooSize, 'awoo');
  const legacyPackage = validateAsset(version, legacyAsset, legacyHash, legacySignature, legacySize, 'bilincm');
  const publishedAt = new Date().toISOString();
  // Schema 2 is a separate entry. Old cores continue seeing the unchanged,
  // immutable profiles.qqmusic schema 1 package; the catalog envelope stays v1.
  return { ...catalog, generatedAt: publishedAt, profiles: { ...catalog.profiles,
    [metadata.catalogKey]: { id: 'qqmusic', version, schemaVersion: metadata.schemaVersion,
      minimumConnectorVersion: metadata.minimumConnectorVersion, ...legacyPackage, publishedAt, awooPackage }
  } };
}

function main(args) {
  if (args[0] === '--metadata' && args.length === 2) {
    console.log(JSON.stringify(profileReleaseMetadata(args[1])));
    return;
  }
  if (args[0] === '--validate-pack' && args.length === 3) {
    console.log(JSON.stringify(validateProfilePack(path.resolve(args[1]), Number(args[2]))));
    return;
  }
  const catalogPath = path.resolve('qqmusic-profile-catalog.json');
  const catalog = JSON.parse(fs.readFileSync(catalogPath, 'utf8'));
  fs.writeFileSync(catalogPath, `${JSON.stringify(updateProfileCatalog(catalog, args), null, 2)}\n`, 'utf8');
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main(process.argv.slice(2));
