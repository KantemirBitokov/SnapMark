# Как рассказать миру о SnapMark — пошаговый план

> **Название.** Есть другие продукты с именем SnapMark (расширения Chrome, приложение iOS). Там, где нужно отличаться
> (каталоги, Product Hunt, заголовки постов), пиши **«SnapMark by SadlyBear»** и сразу давай ссылку на GitHub.

Всё, что можно было подготовить в репозитории, уже лежит здесь. Ниже — шаги, которые делаешь ты сам
(они требуют твоего аккаунта на внешних сайтах). Порядок важен: сначала витрина, потом трафик.

## Шаг 0. Записать демо (30 минут)

По списку в [`docs/media/README.md`](media/README.md): `hero.gif`, `markers.png` и т. д.
Положи файлы в `docs/media/` и раскомментируй строку с `hero.gif` в `README.md` и `README.ru.md`.
Без демо любая публикация работает в разы хуже.

## Шаг 1. Открыть репозиторий

1. GitHub → репозиторий → **Settings → General → Danger Zone → Change visibility → Public**.
2. На главной странице репозитория справа «⚙» у About:
   - Description: `Screenshots with A-B-C markers and comments, series in one Ctrl+C, screen recording with sound. Windows · macOS · Linux.`
   - Website: `https://kantemirbitokov.github.io/SnapMark/` (после шага 2) или `https://sadlybear.pro`
   - Topics: `screenshot` `screenshot-tool` `annotation` `screen-recorder` `bug-report` `avalonia` `dotnet` `windows` `macos` `linux`
3. **Settings → General → Features**: включи **Issues** и **Discussions**.
4. **Settings → General → Social preview** → загрузи картинку 1280×640 (например, `markers.png` с логотипом).

> После открытия репозитория начнёт работать проверка обновлений в программе (версия 1.0.1+)
> и прямые ссылки на файлы на лендинге.

## Шаг 2. Лендинг

**Вариант А — GitHub Pages (бесплатно, 2 минуты):**
Settings → **Pages** → Source: *Deploy from a branch* → Branch: `main`, папка `/docs` → Save.
Через минуту сайт будет на `https://kantemirbitokov.github.io/SnapMark/`.

**Вариант Б — на sadlybear.pro:** залей файлы `docs/index.html` и `docs/icon.png` в папку, например
`sadlybear.pro/snapmark/`. Страница сама подтягивает последнюю версию и ссылки на файлы с GitHub.

## Шаг 3. Магазины пакетов (установка одной командой)

### winget (Windows) — `winget install SadlyBear.SnapMark`
1. Сделай форк https://github.com/microsoft/winget-pkgs
2. Скопируй папку `packaging/winget/manifests/s/SadlyBear/SnapMark/<версия>/` в такую же папку форка.
3. Создай Pull Request. Робот проверит манифест и установщик, модератор примет за 1–7 дней.
4. Для новых версий удобнее утилита `wingetcreate update SadlyBear.SnapMark --version X.Y.Z --urls <ссылка на Setup.exe> --submit`.

### Homebrew (macOS) — `brew install --cask kantemirbitokov/tap/snapmark`
1. Создай публичный репозиторий **`homebrew-tap`** в своём аккаунте.
2. Положи в него `packaging/homebrew/Casks/snapmark.rb` по пути `Casks/snapmark.rb`.
3. Готово — команда выше заработает у всех. (Официальный homebrew-cask принимает программы только после того, как у них наберётся заметная популярность — к этому можно вернуться позже.)

### AUR (Arch / Manjaro) — `yay -S snapmark-bin`
1. Зарегистрируйся на https://aur.archlinux.org и добавь SSH-ключ.
2. `git clone ssh://aur@aur.archlinux.org/snapmark-bin.git`, положи туда `packaging/aur/PKGBUILD`,
   выполни `makepkg --printsrcinfo > .SRCINFO`, закоммить и отправь.

## Шаг 4. Каталоги

| Сайт | Что делать |
|---|---|
| **alternativeto.net** | Add application → тексты в `docs/promo/posts.en.md` → отметить как альтернативу LightShot, ShareX, Greenshot, Snagit, Flameshot, CleanShot X |
| Softpedia | softpedia.com → Submit software (форма внизу сайта) |
| MajorGeeks | majorgeeks.com → Contact → Submit software |
| Uptodown | uptodown.com → Developers → загрузить программу |
| Neowin | neowin.net → Software → Submit news (для релизов) |

## Шаг 5. Сообщества (раз в несколько дней, не всё сразу)

1. **Хабр** — статья `docs/promo/habr.ru.md` (нужна учётная запись; первая статья проходит модерацию в «Песочнице»).
2. **Reddit** — r/software, r/opensource, r/macapps, r/linux, r/QualityAssurance — тексты в `docs/promo/posts.en.md`.
3. **Hacker News** — «Show HN» (там же). Лучшее время — будний день, 15:00–17:00 по Москве.
4. **Product Hunt** — запуск во вторник–четверг, заранее подготовь 3–5 картинок 1270×760.
5. **vc.ru, Pikabu, Telegram-каналы** про софт и тестирование — сокращённая версия статьи для Хабра.
6. **YouTube Shorts / TikTok / Reels** — 30-секундное видео «скриншот → метки → Ctrl+V в чат».

## Шаг 6. Поддерживать интерес

- Отвечай на issues и комментарии — живой автор сильно повышает доверие к неподписанной программе.
- Выпускай обновления: поменять `<Version>` в `src/SnapMark.App/SnapMark.App.csproj`, дописать
  `CHANGELOG.md`, Actions → **Build SnapMark** → **Run workflow**. У пользователей появится уведомление.
- Каждый заметный релиз — повод для нового поста.
