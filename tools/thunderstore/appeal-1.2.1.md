# Отказ Thunderstore: куда писать и что писать

19.09.2026 листинг `Astvard-AstvardServerMod` в сообществе Valheim ушёл в `rejected`
сразу после заливки 1.2.1. На странице пакета только «Package rejected / Invalid
submission» и ссылка в их Discord; в «Manage Package» причины тоже нет — там статус
«Not deprecated» и категории, больше ничего.

## Куда

Discord Thunderstore (https://discord.thunderstore.io/), **канал-форум
`#rejected-uploads`** — не в общий чат и не в личку модератору. Правило канала одно и
написано прямо в форме: **в теме обязательно ссылка на отклонённый пакет.** Создаётся
кнопкой «Новая публикация»: заголовок плюс сообщение.

## Что там видно про такие отказы

Половина тем в канале — те же слова «Invalid submission», и отвечает на них модератор
`753` одной строкой «Approved», перепроверив пакет руками. Полезные примеры на
19.09.2026:

- `GCValheimStats` (мод для Valheim, тот же набор файлов, что у нас): «keeps getting
  rejected with Invalid submission. This seems like a **false positive from the
  scanner**» — разобрано и одобрено;
- `SimpleSell`: «i marked it AI Generated, but it was rejected for **untagged AI
  Generated mod**» — ответ модератора: «Approved». То есть тег стоит, а автоматика
  всё равно ругается;
- `EnhancedValheimVRM`: единственный случай, где дело было в самом пакете — менеджеры
  модов схлопывают вложенные папки, и мод из-за этого не работал. У нас одна папка
  `BepInEx/plugins`, так что это не наш случай.

Отсюда вывод: **это почти наверняка ложное срабатывание проверялки**, а не претензия к
содержимому, и лечится просьбой перепроверить. Длинное оправдание не нужно — нужна
ссылка и короткий состав пакета.

## Текст (он и отправлен)

Заголовок:

```
Invalid submission for AstvardServerMod (Valheim)
```

Сообщение:

```
https://thunderstore.io/c/valheim/p/Astvard/AstvardServerMod/

Version 1.1.0 was approved and listed. After I uploaded 1.2.1 the whole listing went to
"rejected - Invalid submission": the mod is gone from community search and the package
page 404s for anyone who is not logged in. It looks like a scanner false positive.

The zip (206 KB) contains: manifest.json, icon.png (256x256), README.md and
BepInEx/plugins/AstvardServerMod.dll - a single BepInEx plugin (net472). No bundled
dependencies (Jotunn is declared as a dependency, not shipped), no obfuscation, no
installer, nothing else. Tagged Mods + AI Generated, NSFW off.

Source and the same build: https://github.com/official-fedorenko/astvard - the mod lives
in valheim-mod/AstvardServerMod and every version is published as a GitHub release too, so
the DLL in the package can be compared against the source it was built from.

Could you take a look? Thanks!
```

## Если ответят иначе

- **«Add an English description»** — перевести `package.md` (это и есть страница мода на
  Thunderstore) и залить новую версию; заодно поднять номер, потому что опубликованную
  версию править нельзя.
- **«Folder structure»** — у нас одна папка `BepInEx/plugins`, менять нечего, показать
  состав архива.
- **Молчание больше суток** — написать в теме ещё раз, не заводя новую.
