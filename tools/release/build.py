"""Собирает архив мода для раздачи через релизы GitHub.

    python tools/release/build.py

Кладёт рядом Astvard-<версия>.zip: распаковывается прямо в папку с игрой.
Отличается от пакета для Thunderstore тем, что Jotunn.dll лежит внутри — у
менеджера модов он приходит зависимостью, а тут ставят руками. Подробности —
в README.md рядом.
"""

import hashlib
import pathlib
import zipfile

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]

DLL = ROOT / "valheim-mod/AstvardServerMod/bin/Release/net472/AstvardServerMod.dll"
JOTUNN = pathlib.Path(r"C:\Games\steamapps\common\Valheim\BepInEx\plugins\Jotunn.dll")
JOTUNN_LICENSE = HERE / "JOTUNN-LICENSE.txt"
SOURCE = ROOT / "valheim-mod/AstvardServerMod/Plugin.cs"

B = "\\"  # обратный слэш только переменной: экранирование тут уже подводило


def fail(message):
    raise SystemExit(f"НЕ СОБРАНО: {message}")


def version_in_source():
    marker = 'internal const string Version = "'
    for line in SOURCE.read_text(encoding="utf-8-sig").splitlines():
        if marker in line:
            return line.split(marker)[1].split('"')[0]
    fail(f"в {SOURCE.name} не нашлась строка {marker}…")


def readme(version, mod_md5, jotunn_md5):
    return f"""ASTVARD - мод для Valheim, версия {version}
==========================================

Что это
-------
Мод сервера Astvard. Даёт панель управления прямо в игре: постройки по
шаблонам, работа с рельефом, руны. Открывается вместе с инвентарём,
клавишей Tab - над окном инвентаря появится деревянная панель.

Мод НЕ обязателен. На сервер пускают и без него, и обновляться можно
когда удобно: никого не отключают за то, что версия не совпала.


Что нужно до установки
----------------------

1) Valheim из Steam.
   Версия из Game Pass и консоли не подойдут: на сервере выключен
   crossplay, они этот сервер просто не увидят.

2) BepInEx - загрузчик модов. Ставится один раз, дальше не трогать:
   https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/

   В скачанном архиве открой папку BepInExPack_Valheim и всё её
   содержимое положи в папку с игрой - туда, где лежит valheim.exe.
   Найти её проще всего через Steam: правой кнопкой по Valheim,
   Управление -> Просмотреть локальные файлы.


Установка
---------
Содержимое ЭТОГО архива распакуй в ту же папку с игрой. Папка BepInEx
там уже будет после прошлого шага - файлы добавятся внутрь неё.

Должно получиться так:

    Valheim{B}
        valheim.exe
        BepInEx{B}
            plugins{B}
                AstvardServerMod.dll
                Jotunn.dll

Запускать игру как обычно, через Steam.


Как зайти на сервер
-------------------
Сервер скрытый, в общем списке серверов его нет. Адрес, доступ и всё
остальное - на сайте:

    https://astvard.online

Там «Войти через Steam», потом «Запросить доступ». Пока заявку не
одобрили, игра при заходе скажет, что ты ЗАБАНЕН. Это не так - просто
тебя ещё нет в списке.


Как понять, что мод работает
----------------------------
Открой в игре инвентарь (Tab). Над ним должна появиться деревянная
панель с кнопками - «Ознакомиться», «Функции» и другими.

Если панели нет, открой файл
    Valheim{B}BepInEx{B}LogOutput.log
и поищи строки:
    Loading [Jotunn ...]
    Loading [AstvardServerMod {version}]
Если их там нет - BepInEx не установился или файлы легли не в ту папку.

Ещё одна возможная причина: Windows помечает скачанное из интернета.
Нажми на скачанный ZIP правой кнопкой -> Свойства -> внизу галочка
«Разблокировать» -> ОК, и только потом распакуй заново.


Обновления
----------
Новые версии выходят здесь:
    https://github.com/official-fedorenko/astvard/releases

Обновляться необязательно. Если на сервере версия новее, мод один раз
за заход напишет об этом в чат - и всё, играть это не мешает.


Что в архиве
------------
AstvardServerMod.dll   md5 {mod_md5}
Jotunn.dll             md5 {jotunn_md5}

Jotunn - чужая библиотека, без неё мод не запустится. Распространяется
по лицензии MIT, её текст лежит рядом в JOTUNN-LICENSE.txt.
Сайт проекта: https://github.com/Valheim-Modding/Jotunn
"""


def main():
    for path in (DLL, JOTUNN, JOTUNN_LICENSE):
        if not path.exists():
            fail(f"нет файла {path}")

    version = version_in_source()
    body = DLL.read_bytes()

    # Собранная раньше правки версии DLL — это архив, который врёт номером.
    if version.encode("utf-16-le") not in body and version.encode() not in body:
        fail(f"в собранной DLL нет версии {version} — пересобери Release")

    mod_md5 = hashlib.md5(body).hexdigest()
    jotunn_md5 = hashlib.md5(JOTUNN.read_bytes()).hexdigest()

    # BOM и CRLF: файл откроют Блокнотом на чужой Windows, и кириллица должна
    # прочитаться, а строки — не слипнуться в одну.
    text = readme(version, mod_md5, jotunn_md5).replace("\n", "\r\n")
    readme_bytes = b"\xef\xbb\xbf" + text.encode("utf-8")

    out = HERE / f"Astvard-{version}.zip"
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.write(DLL, "BepInEx/plugins/AstvardServerMod.dll")
        zf.write(JOTUNN, "BepInEx/plugins/Jotunn.dll")
        zf.writestr("README.txt", readme_bytes)
        zf.write(JOTUNN_LICENSE, "JOTUNN-LICENSE.txt")

    print(f"{out.name}: {out.stat().st_size // 1024} КБ")
    print(f"  версия             {version}")
    print(f"  AstvardServerMod   {mod_md5}")
    print(f"  Jotunn             {jotunn_md5}")

    # Проверяем то, что чинили дважды: дерево папок в README должно остаться
    # отдельными строками, а не слипнуться из-за концевых слэшей.
    with zipfile.ZipFile(out) as zf:
        back = zf.read("README.txt").decode("utf-8-sig")
        tree = [l for l in back.splitlines() if l.strip().endswith(B)]
        if len(tree) != 3:
            fail(f"в README дерево папок разъехалось: строк со слэшем {len(tree)}")
        for name in zf.namelist():
            print(f"  в архиве           {name}")
    print("  README             дерево папок целое")


if __name__ == "__main__":
    main()
