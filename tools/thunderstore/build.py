"""Собирает пакет для Thunderstore из собранной DLL.

    python tools/thunderstore/build.py

Кладёт рядом AstvardServerMod-<версия>.zip. Ничего никуда не публикует.
Подробности и что делать с архивом — в README.md рядом.
"""

import hashlib
import json
import pathlib
import zipfile

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]

DLL = ROOT / "valheim-mod/AstvardServerMod/bin/Release/net472/AstvardServerMod.dll"
SOURCE = ROOT / "valheim-mod/AstvardServerMod/Plugin.cs"
MANIFEST = HERE / "manifest.json"
ICON = HERE / "icon.png"
PACKAGE_README = HERE / "package.md"


def fail(message):
    raise SystemExit(f"НЕ СОБРАНО: {message}")


def version_in_source():
    """Версия живёт в Plugin.cs и больше нигде; манифест обязан её повторять."""
    marker = 'internal const string Version = "'
    for line in SOURCE.read_text(encoding="utf-8-sig").splitlines():
        if marker in line:
            return line.split(marker)[1].split('"')[0]
    fail(f"в {SOURCE.name} не нашлась строка {marker}…")


def png_size(path):
    """Размер PNG из заголовка IHDR — чтобы не тащить зависимость ради двух чисел."""
    data = path.read_bytes()
    if not data.startswith(b"\x89PNG\r\n\x1a\n"):
        fail(f"{path.name} — не PNG")
    return int.from_bytes(data[16:20], "big"), int.from_bytes(data[20:24], "big")


def main():
    for path in (DLL, MANIFEST, ICON, PACKAGE_README):
        if not path.exists():
            fail(f"нет файла {path}")

    version = version_in_source()
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))

    # Версия в пакете, которая разошлась с версией в DLL, — это ровно тот молчаливый
    # рассинхрон, ради которого всё и затевалось. Лучше не собраться.
    if manifest["version_number"] != version:
        fail(f"manifest.json говорит {manifest['version_number']}, а Plugin.cs — {version}")

    body = DLL.read_bytes()
    if version.encode("utf-16-le") not in body and version.encode() not in body:
        fail(f"в собранной DLL нет версии {version} — пересобери Release")

    width, height = png_size(ICON)
    if (width, height) != (256, 256):
        fail(f"icon.png {width}x{height}, а Thunderstore примет только 256x256")

    if len(manifest["description"]) > 250:
        fail(f"описание {len(manifest['description'])} символов, максимум 250")

    out = HERE / f"AstvardServerMod-{version}.zip"
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.write(MANIFEST, "manifest.json")
        zf.write(ICON, "icon.png")
        zf.write(PACKAGE_README, "README.md")
        # Имя папки решает, куда менеджер положит файл.
        zf.write(DLL, "BepInEx/plugins/AstvardServerMod.dll")

    print(f"{out.name}: {out.stat().st_size // 1024} КБ")
    print(f"  версия      {version}")
    print(f"  зависимости {', '.join(manifest['dependencies'])}")
    print(f"  DLL md5     {hashlib.md5(body).hexdigest()}")
    with zipfile.ZipFile(out) as zf:
        for name in zf.namelist():
            print(f"  в архиве    {name}")


if __name__ == "__main__":
    main()
