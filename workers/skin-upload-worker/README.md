# minivibe skin upload worker

Cloudflare Worker для безопасной загрузки скинов в GitHub без токена внутри лаунчера.

## Что делает

- Принимает `POST /upload-skin`.
- Проверяет общий код доступа.
- Проверяет ник Minecraft: 3-16 символов, латиница, цифры, `_`.
- Проверяет итоговый PNG 64x64 или 64x32 и лимит размера.
- Коммитит файл в репозиторий: `skins/<ник>.png`.

Лаунчер может принять PNG/JPG/JPEG как исходник, но перед загрузкой всегда конвертирует файл в PNG. Для настоящих Minecraft-слоев лучше использовать PNG, потому что JPG не хранит прозрачность.

## Переменные Worker

Обычные переменные:

- `REPO_OWNER`: `wawgame123`
- `REPO_NAME`: `Minecraft`
- `BRANCH`: `main`

Секреты:

- `GITHUB_TOKEN`: fine-grained GitHub token с доступом `Contents: Read and write` только к репозиторию `wawgame123/Minecraft`
- `UPLOAD_SECRET`: код доступа, который будут вводить игроки в лаунчере

## Деплой

1. Установить Wrangler:

   ```powershell
   npm install -g wrangler
   ```

2. Войти в Cloudflare:

   ```powershell
   wrangler login
   ```

3. Скопировать пример конфига:

   ```powershell
   Copy-Item wrangler.toml.example wrangler.toml
   ```

4. Записать секреты:

   ```powershell
   wrangler secret put GITHUB_TOKEN
   wrangler secret put UPLOAD_SECRET
   ```

5. Опубликовать:

   ```powershell
   wrangler deploy
   ```

После деплоя в лаунчере укажите URL:

```text
https://<worker-name>.<account>.workers.dev/upload-skin
```

Для чтения скинов OfflineSkins может использовать raw GitHub URL:

```text
https://raw.githubusercontent.com/wawgame123/Minecraft/main
```

Финальный путь скина:

```text
https://raw.githubusercontent.com/wawgame123/Minecraft/main/skins/Ник.png
```

GitHub Pages не нужен для загрузки. Он нужен только как красивый публичный хостинг для чтения:

```text
https://wawgame123.github.io/Minecraft/skins/Ник.png
```

Если Pages включен, в лаунчере можно заменить базовый URL с raw GitHub на:

```text
https://wawgame123.github.io/Minecraft
```
