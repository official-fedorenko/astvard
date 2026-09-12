"""Packs PNG files into one favicon.ico.

An .ico may hold PNG images as they are, which every browser since IE Vista
reads, so no image library is needed: a six-byte header, one sixteen-byte entry
per image, then the PNG bytes.

    python tools/icons/ico.py client/favicon.ico 16.png 32.png 48.png
"""
import struct
import sys


def png_size(data):
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        raise SystemExit('not a PNG')
    return struct.unpack('>II', data[16:24])


def main(target, sources):
    images = [open(path, 'rb').read() for path in sources]
    header = struct.pack('<HHH', 0, 1, len(images))
    offset = len(header) + 16 * len(images)
    entries, blobs = b'', b''
    for data in images:
        width, height = png_size(data)
        # 0 in the byte means 256: the field is one byte wide.
        entries += struct.pack('<BBBBHHII', width % 256, height % 256, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    with open(target, 'wb') as out:
        out.write(header + entries + blobs)


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2:])
