// The world's passport: _main.<N>.fwl2 next to _main.<N>.ok in the world's folder.
//
// This is the one thing that makes a chosen seed possible at all. There is no -seed
// flag and there never was: a world the server creates gets ten random characters
// from World.GenerateSeed(). But the seed is not magic - it is a string in this file,
// and everything else about the map is grown from it. Write the file, and the server
// grows that map on its first start.
//
// The format is World.SaveWorldFWLData (assembly_valheim.dll 1.0.15), and it was
// checked against a real file rather than trusted: AstwardWorld's _main.1.fwl2 parses
// field for field, and the seed hash we compute for its "iMbvYy6FIt" comes out
// 0x6F78ADD1, the same four bytes the game wrote.

// Version.World of this build. Those same four bytes are the entire content of a
// _main.<N>.ok file - checked on a real one - but we write no .ok: the game does
// not write one for a world nobody has saved yet, and doing more than the game
// does here would be inventing.
const WORLD_VERSION = 41;
// World.m_worldGenVersion for a world made by 1.0. A lower number would make the game
// generate a different map from the same seed - the number IS part of the map.
const WORLD_GEN_VERSION = 2;

// The alphabet World.GenerateSeed() picks from: no letter O and no digit 1, so that a
// seed read aloud or off a screenshot cannot be mistyped into a different world.
const SEED_ALPHABET = 'abcdefghijklmnpqrstuvwxyzABCDEFGHIJKLMNPQRSTUVWXYZ023456789';
const SEED_LENGTH = 10;

// Valheim's own string hash. Not a choice: this exact number is what the terrain
// generator is fed, so any other hash would grow a different world from the same
// letters.
function stableHashCode(str) {
  let num = 5381;
  let num2 = num;
  for (let i = 0; i < str.length; i += 2) {
    num = (((num << 5) + num) ^ str.charCodeAt(i)) | 0;
    if (i === str.length - 1) break;
    num2 = (((num2 << 5) + num2) ^ str.charCodeAt(i + 1)) | 0;
  }
  return (num + Math.imul(num2, 1566083941)) | 0;
}

function generateSeed(random = Math.random) {
  let out = '';
  for (let i = 0; i < SEED_LENGTH; i++) {
    out += SEED_ALPHABET[Math.floor(random() * SEED_ALPHABET.length)];
  }
  return out;
}

// ZPackage strings are BinaryWriter strings: a 7-bit encoded byte length, then UTF-8.
function writeString(chunks, value) {
  const bytes = Buffer.from(value, 'utf8');
  let len = bytes.length;
  const head = [];
  while (len >= 0x80) {
    head.push((len & 0x7f) | 0x80);
    len >>>= 7;
  }
  head.push(len);
  chunks.push(Buffer.from(head), bytes);
}

function readString(buf, pos) {
  let len = 0;
  let shift = 0;
  for (;;) {
    const b = buf[pos++];
    if (b === undefined) throw new Error('файл кончился посреди строки');
    len |= (b & 0x7f) << shift;
    if ((b & 0x80) === 0) break;
    shift += 7;
    if (shift > 28) throw new Error('длина строки не по формату');
  }
  const value = buf.toString('utf8', pos, pos + len);
  return [value, pos + len];
}

function writeInt(chunks, value) {
  const b = Buffer.alloc(4);
  b.writeInt32LE(value | 0);
  chunks.push(b);
}

// The world's id. name.GetStableHashCode() + Utils.GenerateUID(), and the second half
// is random in the game too - nothing reads it back for meaning, it only has to
// differ between worlds so two saves are never taken for one.
function makeUid(name, random = Math.random) {
  const low = BigInt(stableHashCode(name));
  const noise = BigInt(Math.floor(random() * 0x7fffffff));
  return (low + (noise << 16n)) & 0x7fffffffffffffffn;
}

// The bytes of _main.0.fwl2 for a world nobody has played yet: no database, no world
// modifiers, no player history. Everything the game would have written on the first
// start, except that we choose the seed and it would not have.
function buildWorldPassport({ name, seed, uid }) {
  if (!name) throw new Error('имя мира пустое');
  if (!seed) throw new Error('сид пустой');

  const body = [];
  writeInt(body, WORLD_VERSION);
  writeString(body, name);
  writeString(body, seed);
  writeInt(body, stableHashCode(seed));
  const id = Buffer.alloc(8);
  id.writeBigInt64LE(uid === undefined ? makeUid(name) : BigInt(uid));
  body.push(id);
  writeInt(body, WORLD_GEN_VERSION);
  // m_needsDB: false. The database is what a played world has; this one has nothing
  // to load, and the server writes its first on the first save.
  body.push(Buffer.from([0]));
  writeInt(body, 0); // starting global keys - the modifiers, and we set none
  writeInt(body, 0); // player history

  const pkg = Buffer.concat(body);
  const head = Buffer.alloc(4);
  head.writeInt32LE(pkg.length);
  return Buffer.concat([head, pkg]);
}

// What a .fwl2 (or an old .fwl) says about itself. Used to read a world file the
// owner has on his computer: the answer we are after is its seed.
function readWorldPassport(buffer) {
  if (!Buffer.isBuffer(buffer) || buffer.length < 12) throw new Error('это не файл мира');
  const declared = buffer.readInt32LE(0);
  if (declared <= 0 || declared > buffer.length - 4) throw new Error('это не файл мира');
  const pkg = buffer.subarray(4, 4 + declared);

  const version = pkg.readInt32LE(0);
  let pos = 4;
  let name;
  let seed;
  [name, pos] = readString(pkg, pos);
  [seed, pos] = readString(pkg, pos);
  const seedHash = pkg.readInt32LE(pos);
  pos += 4;
  const uid = pkg.readBigInt64LE(pos);
  pos += 8;
  // WorldGenVersion and NeedsDB arrived in later versions; an old file simply ends
  // earlier, and guessing past the end would invent numbers.
  const worldGenVersion = pos + 4 <= pkg.length ? pkg.readInt32LE(pos) : 0;

  return {
    version,
    name,
    seed,
    seedHash,
    uid: uid.toString(),
    worldGenVersion,
    // The same seed with a different generator version is a different map, and the
    // one thing worth saying out loud about a file from who knows which year.
    sameGenerator: worldGenVersion === WORLD_GEN_VERSION
  };
}

module.exports = {
  WORLD_VERSION,
  WORLD_GEN_VERSION,
  SEED_ALPHABET,
  SEED_LENGTH,
  stableHashCode,
  generateSeed,
  makeUid,
  buildWorldPassport,
  readWorldPassport
};
