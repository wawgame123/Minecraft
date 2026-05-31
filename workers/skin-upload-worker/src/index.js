const MAX_SKIN_BYTES = 512 * 1024;
const PLAYER_NAME_RE = /^[A-Za-z0-9_]{3,16}$/;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method === "OPTIONS") {
      return withCors(new Response(null, { status: 204 }));
    }

    if (request.method === "GET" && url.pathname === "/health") {
      return json({ ok: true });
    }

    if (request.method !== "POST" || url.pathname !== "/upload-skin") {
      return json({ error: "Not found" }, 404);
    }

    try {
      return withCors(await uploadSkin(request, env));
    } catch (error) {
      return json({ error: error.message || "Upload failed" }, 400);
    }
  }
};

async function uploadSkin(request, env) {
  requireEnv(env, "GITHUB_TOKEN");
  requireEnv(env, "UPLOAD_SECRET");

  const form = await request.formData();
  const playerName = String(form.get("playerName") || "").trim();
  const secret = String(form.get("secret") || "");
  const skin = form.get("skin");

  if (secret !== env.UPLOAD_SECRET) {
    throw new Error("Invalid upload code");
  }

  if (!PLAYER_NAME_RE.test(playerName)) {
    throw new Error("Invalid player name");
  }

  if (!(skin instanceof File)) {
    throw new Error("Skin file is missing");
  }

  if (skin.size <= 0 || skin.size > MAX_SKIN_BYTES) {
    throw new Error("Skin file is too large");
  }

  const bytes = new Uint8Array(await skin.arrayBuffer());
  const dimensions = readPngDimensions(bytes);
  if (dimensions.width !== 64 || ![32, 64].includes(dimensions.height)) {
    throw new Error("Skin must be PNG 64x64 or 64x32");
  }

  const owner = env.REPO_OWNER || "wawgame123";
  const repo = env.REPO_NAME || "Minecraft";
  const branch = env.BRANCH || "main";
  const path = `skins/${playerName}.png`;
  const existing = await getExistingFileSha(env, owner, repo, path, branch);
  const content = bytesToBase64(bytes);

  const response = await fetch(`https://api.github.com/repos/${owner}/${repo}/contents/${encodeURIComponentPath(path)}`, {
    method: "PUT",
    headers: githubHeaders(env),
    body: JSON.stringify({
      message: `Update skin for ${playerName}`,
      branch,
      content,
      ...(existing ? { sha: existing } : {})
    })
  });

  if (!response.ok) {
    throw new Error(`GitHub commit failed: ${response.status} ${await response.text()}`);
  }

  return json({
    ok: true,
    path,
    rawUrl: `https://raw.githubusercontent.com/${owner}/${repo}/${branch}/${path}`
  });
}

async function getExistingFileSha(env, owner, repo, path, branch) {
  const response = await fetch(
    `https://api.github.com/repos/${owner}/${repo}/contents/${encodeURIComponentPath(path)}?ref=${encodeURIComponent(branch)}`,
    { headers: githubHeaders(env) }
  );

  if (response.status === 404) {
    return null;
  }

  if (!response.ok) {
    throw new Error(`GitHub lookup failed: ${response.status} ${await response.text()}`);
  }

  const body = await response.json();
  return body.sha || null;
}

function readPngDimensions(bytes) {
  const signature = [137, 80, 78, 71, 13, 10, 26, 10];
  for (let i = 0; i < signature.length; i += 1) {
    if (bytes[i] !== signature[i]) {
      throw new Error("Worker accepts committed skins only as PNG");
    }
  }

  return {
    width: readUint32(bytes, 16),
    height: readUint32(bytes, 20)
  };
}

function readUint32(bytes, offset) {
  return (
    (bytes[offset] << 24) |
    (bytes[offset + 1] << 16) |
    (bytes[offset + 2] << 8) |
    bytes[offset + 3]
  ) >>> 0;
}

function bytesToBase64(bytes) {
  let binary = "";
  const chunkSize = 0x8000;
  for (let index = 0; index < bytes.length; index += chunkSize) {
    binary += String.fromCharCode(...bytes.subarray(index, index + chunkSize));
  }

  return btoa(binary);
}

function githubHeaders(env) {
  return {
    "Accept": "application/vnd.github+json",
    "Authorization": `Bearer ${env.GITHUB_TOKEN}`,
    "Content-Type": "application/json",
    "User-Agent": "minivibe-skin-worker",
    "X-GitHub-Api-Version": "2022-11-28"
  };
}

function encodeURIComponentPath(path) {
  return path.split("/").map(encodeURIComponent).join("/");
}

function requireEnv(env, key) {
  if (!env[key]) {
    throw new Error(`${key} is not configured`);
  }
}

function json(body, status = 200) {
  return withCors(new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8" }
  }));
}

function withCors(response) {
  const next = new Response(response.body, response);
  next.headers.set("Access-Control-Allow-Origin", "*");
  next.headers.set("Access-Control-Allow-Methods", "GET,POST,OPTIONS");
  next.headers.set("Access-Control-Allow-Headers", "Content-Type");
  return next;
}
