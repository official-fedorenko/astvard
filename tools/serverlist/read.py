"""Показывает список серверов клиента Valheim так, как его читает сама игра.

    python tools/serverlist/read.py                 # избранное и недавние
    python tools/serverlist/read.py <файл|папка>    # что-то одно

Нужен он ради одного вопроса: под каким именем у клиента записан наш сервер и
по какому ключу это имя лежит. Имя в списке серверов игра берёт из этого файла
(`MultiBackendMatchmaking.TryGetServerName`), а обновить его может только
матчмейкинг Steam — то есть никогда, пока сервер поднят с `-public 0`. Отсюда
и «наш сервер называется именем прежнего жильца IP»: имя закешировано с тех
пор, когда по этому адресу отвечал кто-то другой.

Формат снят с `LocalServerList` из `assembly_valheim.dll` 1.0.15, а не угадан:
4 байта длины пакета, `uint` версия (0, 1 или 2), `int` число записей, дальше
на запись — строка вида, строка имени и полезная часть, своя у каждого вида.
У выделенного сервера это строка хоста и `uint` порт, и **ключ — именно они
двое**: `ServerJoinDataDedicated.Equals` сравнивает `m_host` и `m_port`, а
«хост:порт» одной строкой — это только `ToString()`.

Строки — как их пишет BinaryWriter: длина переменной ширины, потом UTF-8.
"""

import pathlib
import struct
import sys

DEFAULT_DIR = pathlib.Path.home() / "AppData/LocalLow/IronGate/Valheim/serverlist_local"
FILES = ("favorite", "recent")


def read_length(data, at):
    """Длина строки у BinaryWriter: по семь бит на байт, старший бит — «есть ещё»."""
    value = 0
    shift = 0
    while True:
        byte = data[at]
        at += 1
        value |= (byte & 0x7F) << shift
        if not byte & 0x80:
            return value, at
        shift += 7


def read_string(data, at):
    length, at = read_length(data, at)
    return data[at:at + length].decode("utf-8", "replace"), at + length


def read_entries(path):
    data = path.read_bytes()
    # Первые четыре байта пишет FileHelpers — это длина самого пакета.
    version, = struct.unpack_from("<I", data, 4)
    count, = struct.unpack_from("<i", data, 8)
    at = 12

    entries = []
    for _ in range(count):
        kind, at = read_string(data, at)
        name, at = read_string(data, at)

        if kind == "Dedicated":
            if version == 0:
                host_raw, port = struct.unpack_from("<II", data, at)
                at += 8
                host = ".".join(str((host_raw >> shift) & 0xFF) for shift in (0, 8, 16, 24))
            else:
                host, at = read_string(data, at)
                port, = struct.unpack_from("<I", data, at)
                at += 4
            entries.append((kind, name, host, port))
            continue

        # Чужие виды записей нам не нужны, но пропустить их надо точно, иначе
        # всё, что лежит после, прочитается мусором.
        if kind == "Steam user":
            at += 8
        elif kind == "PlayFab user":
            _, at = read_string(data, at)
        else:
            entries.append((kind, name, "?", 0))
            break
        if version == 2:
            _, at = read_string(data, at)
        entries.append((kind, name, "—", 0))

    return version, entries


def show(path):
    if not path.exists():
        print(f"{path}: нет файла")
        return

    version, entries = read_entries(path)
    print(f"{path.name}: версия {version}, записей {len(entries)}")
    for kind, name, host, port in entries:
        where = f"{host}:{port}" if port else host
        print(f"   {kind:<12} {where:<28} имя: {name}")
    print()


def main():
    target = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_DIR
    if target.is_dir():
        for name in FILES:
            show(target / name)
    else:
        show(target)


if __name__ == "__main__":
    main()
