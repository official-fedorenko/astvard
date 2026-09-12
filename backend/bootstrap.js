const { parseSteamId64 } = require('./steamid');
const {
  findUserBySteamId,
  createSteamUser,
  updateUserRole,
  countSuperadmins,
} = require('./users');
const { grantWhitelist, setServerAdmin } = require('./whitelist');
const { fetchPersonaName, fallbackNickname } = require('./steam-profile');

// A fresh deployment starts with an empty database, and every road to the admin
// page begins with a rank only a superadmin can hand out. Without this the first
// one has to be written in by hand with psql, on the very machine where the least
// should be done by hand.
//
// It works off SUPERADMIN_STEAM_ID in .env — a Steam number rather than a password,
// because that is how the site signs people in and because a password in .env is a
// password lying around.
//
// It fires only while the site has no superadmin at all. A restart must never undo
// a deliberate demotion; the point is that the site is never left with nobody who
// can let players in — not that this one account is superadmin forever.
async function ensureSuperadmin() {
  const raw = process.env.SUPERADMIN_STEAM_ID;
  if (!raw) return;

  const parsed = parseSteamId64(raw);
  if (parsed.error) {
    console.error(`SUPERADMIN_STEAM_ID: ${parsed.error}. Суперадмин не создан.`);
    return;
  }

  const already = await countSuperadmins();
  if (already > 0) {
    console.log(`Суперадмин уже есть (${already}), SUPERADMIN_STEAM_ID не понадобился.`);
    return;
  }

  let user = await findUserBySteamId(parsed.id);
  if (!user) {
    // The name is a convenience, like everywhere else it is fetched: Steam being
    // slow or the profile being private must not leave the site without an admin.
    const persona = await fetchPersonaName(parsed.id);
    user = await createSteamUser({
      nickname: persona || fallbackNickname(parsed.id),
      steamId: parsed.id,
      // Nobody has signed in as this account yet, so Steam has not vouched for the
      // number — it was typed into .env. The flag flips at the first Steam sign-in.
      verified: false,
    });
    if (!user) {
      console.error('Не удалось завести аккаунт суперадмина — занят никнейм?');
      return;
    }
  }

  // The first superadmin is the owner of the server: rights on the site, a place in
  // permittedlist.txt and admin in the game. actorId is null — nobody granted this,
  // the deployment did.
  const promoted = await updateUserRole(user.id, 'superadmin');
  await grantWhitelist({ userId: user.id, actorId: null });
  await setServerAdmin(user.id, true);
  console.log(
    `Суперадмин заведён: ${promoted.nickname}, SteamID ${parsed.id}. `
    + 'Вход — «Войти через Steam» этим аккаунтом.'
  );
}

module.exports = { ensureSuperadmin };
