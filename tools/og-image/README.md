# og:image

`client/img/astvard-og.png` — картинка, которую показывают Telegram, Discord и
поисковики, когда кто-то кидает ссылку на сайт. Она **растровая и по абсолютному
адресу** в `<meta>`: SVG в превью не понимает ни Telegram, ни Discord, а
относительный путь часть сборщиков не разворачивает.

Исходник — `og.html`, обычная страница ровно 1200×630. Правится как вёрстка, потом
снимается headless-браузером. Из корня репозитория:

```powershell
& "C:\Program Files\Google\Chrome\Application\chrome.exe" --headless=new --disable-gpu `
  --hide-scrollbars --force-device-scale-factor=1 --virtual-time-budget=5000 `
  --screenshot="client\img\astvard-og.png" --window-size=1200,630 `
  "file:///$PWD/tools/og-image/og.html"
```

`--headless=new` обязателен: со старым `--headless` Chrome молча не создаёт файл.
`--virtual-time-budget` нужен, чтобы дождаться шрифта с Google Fonts, иначе снимок
уедет на системном шрифте. Размер холста задан в самом `og.html` (`html,body`), так
что `--window-size` должен совпадать с ним.

Знак и фон картинка берёт из `client/img/astvard-hero.svg` — того же файла, что стоит
в герое главной. Меняешь герб — перерисовывается и превью.
