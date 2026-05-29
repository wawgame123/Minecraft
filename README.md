# WawGame Minecraft Launcher

WPF-лаунчер для серверной сборки Minecraft. Он читает `manifest.json`, проверяет только обязательные файлы из manifest, скачивает отсутствующие или поврежденные файлы и не удаляет пользовательские моды.

Manifest для игроков:

```text
https://raw.githubusercontent.com/wawgame123/Minecraft/main/manifest.json
```

## Что уже реализовано

- загрузка `manifest.json` по URL, `file://` или локальному пути;
- локальные настройки в `%APPDATA%\ServerLauncher\settings.json`;
- выбор папки установки сборки;
- проверка `size` и `sha256`;
- восстановление только файлов из `requiredFiles` с `required: true`;
- опциональная докачка `optionalShaders`, если включен переключатель "Шейдеры";
- список файлов и их статусов;
- changelog/news;
- вкладка BlueMap через встроенный `WebBrowser` и кнопка открытия во внешнем браузере;
- запуск Java-процесса после успешной проверки файлов, если в manifest задан блок `launch`.

## Запуск разработки

```powershell
dotnet run
```

## Manifest

Основной файл создается командой:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Generate-Manifest.ps1
```

Файлы сборки лежат в `server-pack/neoforge-21.1.228`. В manifest попадают все файлы сборки, кроме `shaderpacks`: они записываются в `optionalShaders` и скачиваются только при включенном переключателе "Шейдеры".

Необязательные клиентские моды не входят в серверную обязательную синхронизацию: EMI, Forgematica/Litematica, Light Overlay и Replay/Reforged PlayMod.

Блок `launch` расширяет пример из промпта. Он нужен, чтобы лаунчер знал Java main class, classpath и аргументы запуска. Поддерживаются токены:

- `${game_directory}`
- `${player_name}`
- `${version_name}`
- `${loader}`
- `${loader_version}`

Без `launch.mainClass` лаунчер проверит и восстановит файлы, но покажет понятную ошибку вместо слепого запуска неизвестной команды.
