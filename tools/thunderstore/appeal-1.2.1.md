# Обращение к модераторам Thunderstore

Отправлять с аккаунта хозяина в Discord Thunderstore (https://discord.thunderstore.io/),
в канал поддержки/модерации. Текст по-английски — модераторы англоязычные.

---

Hi! I'm the owner of the **Astvard** team. Our Valheim package has been rejected with
"Invalid submission" and I'd like to find out what exactly to fix.

Package: https://thunderstore.io/c/valheim/p/Astvard/AstvardServerMod/ (`Astvard-AstvardServerMod`)

**What happened.** Version 1.1.0 was uploaded a few days ago and the listing was approved and
searchable. On 19 September I uploaded 1.2.1 and the whole listing went to "rejected": the
package page now 404s for anyone who is not logged in, and the mod is gone from the community
search. The upload form was filled in exactly as it was for 1.1.0 — team `Astvard`, community
Valheim, categories `Mods` and `AI Generated`, NSFW off.

**What is inside the package** (206 KB):

- `manifest.json` — name `AstvardServerMod`, version 1.2.1, one dependency:
  `ValheimModding-Jotunn-2.30.1`
- `icon.png` — 256x256
- `README.md`
- `BepInEx/plugins/AstvardServerMod.dll` — a single BepInEx plugin assembly

No bundled dependencies (Jötunn is declared, not shipped), no obfuscation, no installer, no
other files.

**What the mod is.** An in-game admin panel for our own Valheim server: blueprints, terrain
tools, chest sorting, an in-game currency. It requires BepInEx and Jötunn. The full source is
public — https://github.com/official-fedorenko/astvard, the mod lives in
`valheim-mod/AstvardServerMod`, and the same build is published as a GitHub release, so the
DLL in the package can be compared against the source it was built from.

**One thing I can guess at:** the description and the README are in Russian, because the
players on our server are Russian-speaking. If that is what makes the submission invalid, I
will gladly add an English version — I just can't tell from "Invalid submission" alone.

Could you tell me what specifically is wrong, so I can fix it and re-submit? Happy to provide
anything else you need — build logs, the exact zip, whatever helps.

Thanks!
