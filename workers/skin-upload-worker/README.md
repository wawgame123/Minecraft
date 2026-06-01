# minivibe skin upload worker

Cloudflare Worker for uploading player skins to GitHub without putting the GitHub token inside the launcher.

## What It Does

- Accepts `POST /upload-skin` as JSON from the launcher.
- Validates Minecraft player name: 3-16 chars, latin letters, digits, `_`.
- Accepts the final base64 PNG produced by the launcher.
- Validates PNG size: 64x64 or 64x32.
- Commits the file to `skins/<name>.png`.

The launcher can select PNG/JPG/JPEG, but it converts everything to PNG before upload. PNG is the proper format for Minecraft skins because JPG has no transparency for overlay layers.

## Security Note

This worker is public. Players do not need a password.

That means anyone who knows the worker URL can upload or replace `skins/<name>.png`. This is convenient for a small private circle, but it is not strong identity protection.

## Worker Variables

Plain variables:

- `REPO_OWNER`: `wawgame123`
- `REPO_NAME`: `Minecraft`
- `BRANCH`: `main`

Secret:

- `GITHUB_TOKEN`: fine-grained GitHub token with `Contents: Read and write` for `wawgame123/Minecraft`

## Deploy With Wrangler

```powershell
npm install -g wrangler
wrangler login
Copy-Item wrangler.toml.example wrangler.toml
wrangler secret put GITHUB_TOKEN
wrangler deploy
```

After deploy, launcher upload URL:

```text
https://<worker-name>.<account>.workers.dev/upload-skin
```

OfflineSkins raw GitHub base URL:

```text
https://raw.githubusercontent.com/wawgame123/Minecraft/main
```

Final skin URL:

```text
https://raw.githubusercontent.com/wawgame123/Minecraft/main/skins/<name>.png
```

GitHub Pages is optional. It is only a nicer public host for reading skins:

```text
https://wawgame123.github.io/Minecraft/skins/<name>.png
```
