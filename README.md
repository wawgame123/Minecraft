# minivibe

Лаунчер и manifest для своей сборки.

Manifest:

```text
https://raw.githubusercontent.com/wawgame123/Minecraft/main/manifest.json
```

## Manifest

Основной файл создается командой:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Generate-Manifest.ps1
```

Файлы сборки лежат в `server-pack/neoforge-21.1.228`. В manifest попадают все файлы сборки, кроме `shaderpacks`: они записываются в `optionalShaders` и скачиваются только при включенном переключателе "Шейдеры".

Необязательные клиентские моды не входят в серверную обязательную синхронизацию: EMI, Forgematica/Litematica, Light Overlay и Replay/Reforged PlayMod.
