# Иконки сайта

Всё растровое, что поисковики, браузеры и телефоны берут вместо `astvard-mark.svg`:

| Файл | Кто берёт |
|---|---|
| `client/favicon.ico` (16, 32, 48) | вкладка браузера и выдача Яндекса — он ищет файл по этому адресу сам |
| `client/img/astvard-icon-180.png` | ярлык на экране iPhone (`apple-touch-icon`) |
| `client/img/astvard-icon-192.png`, `-512.png` | `site.webmanifest` для Android и логотип в разметке schema.org |

SVG-иконку понимают не все: Safari и часть сборщиков превью её пропускают, а Яндекс
в выдаче показывает то, что лежит в `/favicon.ico`.

Исходники — две страницы: `favicon.html` (знак во весь кадр на прозрачном) и
`tile.html` (знак на тёмном фоне с полями — iOS заливает прозрачное чёрным, Android
обрезает кругом). Размер картинки — это размер окна снимка. Из корня репозитория, в
Git Bash:

```bash
CHROME="/c/Program Files/Google/Chrome/Application/chrome.exe"
OUT=$(mktemp -d)
shot() { "$CHROME" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 \
  --default-background-color=00000000 --screenshot="$(cygpath -w "$2")" --window-size=$3,$3 \
  "file:///$(cygpath -m "$PWD/tools/icons/$1")"; }
for s in 16 32 48; do shot favicon.html "$OUT/$s.png" $s; done
for s in 180 192 512; do shot tile.html "client/img/astvard-icon-$s.png" $s; done
python tools/icons/ico.py client/favicon.ico "$OUT/16.png" "$OUT/32.png" "$OUT/48.png"
```

`--default-background-color=00000000` нужен ради прозрачности: без него Chrome
кладёт под страницу белый лист, и у фавиконки появляются белые углы.
