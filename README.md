# SnapMark

Скриншоты с метками A-B-C и комментариями, серии скриншотов для отчётов, запись экрана.
Windows 10/11 — основная платформа; архитектура готова к порту на macOS и Linux.

## Сборка

Нужно: **Visual Studio 2022** (17.8+) с нагрузкой «Разработка классических приложений .NET» или **.NET 8 SDK**.

```
dotnet restore
dotnet run --project src/SnapMark.App
```

В Visual Studio: открыть `SnapMark.sln` → стартовый проект **SnapMark.App** → F5.
Для красивой правки XAML поставь расширение **Avalonia for Visual Studio** (необязательно).

Публикация одним exe:

```
dotnet publish src/SnapMark.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Для записи видео положи `ffmpeg.exe` рядом с `SnapMark.exe` (сборки: gyan.dev/ffmpeg/builds, release essentials)
или укажи путь в Настройки → Видео.

## Релиз: установщик и портативная версия

Один раз: `winget install JRSoftware.InnoSetup`. Потом из корня репозитория:

```
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
```

В папке `release\` появятся `SnapMark-<версия>-Setup.exe` (установщик, без прав администратора,
6 языков, ярлыки, автозапуск по желанию) и `SnapMark-<версия>-portable.zip`.
Версия берётся из `<Version>` в `src/SnapMark.App/SnapMark.App.csproj`. Скрипт установщика — `installer/SnapMark.iss`.

## macOS и Linux

| | Windows | macOS | Linux (X11) | Linux (Wayland) |
|---|---|---|---|---|
| Скриншоты и редактор | ✓ | ✓ (нужно разрешение «Запись экрана») | ✓ | ✓ через grim / gnome-screenshot / spectacle |
| Глобальные клавиши | ✓ PrtSc | ✓ ⌘⇧1 / ⌘⇧2 / ⌘⇧9 | ✓ (если PrtSc не занят системой) | системное сочетание → `snapmark --region` |
| Копирование картинка + текст | ✓ умная вставка | ✓ | ✓ | ✓ |
| Видео | ✓ звук ПК + микрофон | ✓ микрофон + звук ПК (macOS 13+ встроенно, 11–12 — через BlackHole) | ✓ | — (только X11) |
| Автозапуск | ✓ | ✓ LaunchAgent | ✓ ~/.config/autostart | ✓ |

Команды (для системных сочетаний на Mac/Wayland и для скриптов): `snapmark --region`, `--full`, `--record`, `--series`, `--settings`.
Если SnapMark уже запущен, команда передаётся ему.

Сборка:
- **macOS** — `./build-macos.sh` на Mac → `release/SnapMark-<версия>-macOS-arm64.dmg` и `-x64.dmg`.
- **Linux** — `./build-linux.sh` на Linux или в WSL2 → AppImage, .deb, .tar.gz.
- **Всё сразу без своих Mac/Linux** — GitHub Actions (`.github/workflows/release.yml`): Actions → Build SnapMark → Run workflow,
  или `git tag v1.0.0 && git push --tags` — файлы появятся в GitHub Release.

ffmpeg на Mac/Linux: `brew install ffmpeg` / `sudo apt install ffmpeg`.

## Языки интерфейса

English (по умолчанию), Русский, Deutsch, Français, Italiano, Español — Настройки → Основные → Язык (после смены — перезапуск).

Как устроено: ключ перевода — исходная русская строка. В C# — `L.T("Текст")` / `L.F("Серия: {0}", n)`,
в XAML — `Text="{l:Tr 'Текст'}"`. Переводы — `src/SnapMark.Core/Strings.tsv` (колонки ru, en, de, fr, it, es).
Новая строка без перевода показывается по-английски, а если нет и его — как есть.

## Что умеет каркас

| Функция | Где в коде |
|---|---|
| PrtSc / свои сочетания, глобально | `Platform/Windows/WinHotkeys.cs` |
| Выделение области, весь экран (двойной клик или Shift+PrtSc), несколько мониторов | `Capture/CaptureSurface.cs`, `CaptureController.cs` |
| Markup-инструменты: перо (толщина по скорости/нажиму), карандаш, маркер, ластик; фигуры; текст | `Annotations/Annotations.cs` |
| Распознавание фигур: нарисуй от руки и задержи — станет ровной линией, прямоугольником или эллипсом | `Annotations/ShapeRecognizer.cs` |
| Метки A, B, C… с хвостом-стрелкой и без, комментарии к меткам | `MarkerAnnotation`, панель в `OverlayWindow` |
| Цвет, толщина, размер, жирный/курсив, подложка, пунктир, заливка | панель стиля в `OverlayWindow` |
| Undo/Redo, перемещение, дублирование и удаление объектов (Delete, ПКМ, ластик, × у метки) | `AnnotationDocument.cs` |
| Умная вставка: одно Ctrl+V — картинки и текст в чат/мессенджер, картинки с подписями в Word | `Services/SmartPaste.cs` |
| Копировать, сохранить, сохранить как (PNG/JPG/WebP/BMP), печать, поиск в Google Lens | `OverlayWindow.axaml.cs` |
| Серия скриншотов → копирование: картинки + текст `Screenshot_1_A — комментарий` | `Series/SeriesService.cs`, `UI/SeriesWindows.cs` |
| Запись видео области: MP4/MKV/WebM/GIF, микрофон | `Recording/`, `Platform/Common/FfmpegRecorder.cs` |
| Настройки в стиле macOS | `Settings/SettingsWindow.axaml` |

### Горячие клавиши в редакторе

`V` выбор · `P` перо · `C` карандаш · `H` маркер · `E` ластик · `S` фигура · `L` линия · `A` стрелка ·
`D` двойная стрелка · `R` прямоугольник · `O` эллипс · `T` текст · `M` метка · `N` метка со стрелкой ·
`Shift` — шаг 45° / квадрат · ПКМ по объекту — удалить ·
`Ctrl+C` копировать · `Ctrl+S` быстро сохранить · `Ctrl+Shift+S` сохранить как · `Ctrl+P` печать ·
`Ctrl+G` Google · `Ctrl+D` в серию · `Ctrl+R` видео области · `Ctrl+J` дублировать · `Ctrl+Z/Y` · `Del` · `Esc`.
Сочетания с Ctrl работают и когда курсор стоит в поле комментария.

### Как работает серия

1. Скриншот → метки → комментарии в панели справа → `Ctrl+D` («в серию»).
2. Повторить сколько нужно. Внизу экрана — панель серии (в скриншоты не попадает).
3. `Ctrl+C` в редакторе (или «Копировать всё» на панели).
4. Одно `Ctrl+V` куда угодно — **умная вставка** подстроит буфер под приложение:
   браузер/мессенджер — сначала картинки, следом текст; Word/Outlook/WordPad — RTF с картинками и подписями;
   Проводник — файлы; Блокнот — текст. Если буфер перезаписать чем-то другим, умная вставка отключается сама.

## PrtSc и «Ножницы» Windows 11

LightShot ничего особого не делает: он просто регистрирует PrtSc, а на Windows 11 пользователь должен сам
отключить «Использовать PrtSc для открытия ножниц». SnapMark перехватывает PrtSc низкоуровневым хуком
клавиатуры (как ShareX/PowerToys), а если «Ножницы» всё равно открываются — сам находит конфликт и в
Настройки → Горячие клавиши предлагает отключить его одной кнопкой.

## Порт на macOS / Linux

Всё зависящее от ОС — за интерфейсами в `Platform/IPlatform.cs`. Сейчас в `Platform/Unix` упрощённые реализации:

| Что | macOS | Linux |
|---|---|---|
| Захват | `screencapture` → ScreenCaptureKit | `grim` / `import` → xdg-desktop-portal |
| Глобальные клавиши | TODO: Carbon `RegisterEventHotKey` | TODO: portal GlobalShortcuts / X11 |
| Буфер обмена | только текст → TODO: NSPasteboard | только текст → TODO: wl-copy / xclip |
| Видео | ffmpeg avfoundation | ffmpeg x11grab / pulse |

UI (Avalonia) общий для всех ОС, его переписывать не нужно.

## Дальше по плану

- Системный звук в записи (WASAPI loopback) и захват через `ddagrab` для 60 fps.
- Размытие/пикселизация области, «лупа» при выделении, выделение окна под курсором.
- Повторное редактирование кадра из серии.
- Установщик (MSIX / Inno Setup), автообновление.
