const USERNAME_RE = /^[A-Za-z0-9_]{3,16}$/;
const DEFAULT_PASSWORD_ITERATIONS = 100000;
const DEFAULT_SESSION_DAYS = 30;
const ONLINE_ACTIVITY_WINDOW_MS = 45 * 1000;
const RELAY_SESSION_TTL_MS = 30 * 1000;
const RELAY_CONNECTION_TTL_MS = 90 * 1000;
const RELAY_TRANSPORT_MODE = "cfws";
const encoder = new TextEncoder();
const GITHUB_API_BASE_URL = "https://api.github.com";
const GITHUB_API_VERSION = "2022-11-28";
const GITHUB_AVATAR_PATH_PREFIX = "avatars/by-username";
const GITHUB_SKIN_PATH_PREFIX = "skins/by-username";
const MAX_AVATAR_BYTES = 1024 * 1024;

function readEnvString(inputEnv, names, fallback = null) {
  for (const name of names) {
    const value = inputEnv?.[name];
    if (typeof value === "string" && value.trim().length > 0) {
      return value.trim();
    }
  }

  return fallback;
}

function splitGitHubRepository(value) {
  if (typeof value !== "string" || !value.includes("/")) {
    return null;
  }

  const parts = value
    .split("/")
    .map((part) => part.trim())
    .filter(Boolean);

  return parts.length === 2 ? { owner: parts[0], repo: parts[1] } : null;
}

export async function handleFetch(request, inputEnv = {}) {
  const env = createRuntimeEnv(inputEnv);
  const origin = request.headers.get("Origin") || "*";
  const url = new URL(request.url);

  if (request.method === "OPTIONS") {
    return withCors(new Response(null, { status: 204 }), origin);
  }

  try {
    if (request.method === "GET" && url.pathname === "/health") {
      return json(
        {
          ok: true,
          service: "vesper-account-api",
          runtime: env.RUNTIME_NAME,
          storageMode: env.ACCOUNT_STORE_MODE,
          assetStorageMode: isGitHubAssetStorageEnabled(env) ? "github" : "d1",
          avatarStorageMode: getAvatarStorageMode(env),
        },
        200,
        origin
      );
    }

    if (request.method === "POST" && url.pathname === "/api/v1/auth/register") {
      return withCors(await handleRegister(request, env), origin);
    }

    if (request.method === "POST" && url.pathname === "/api/v1/auth/login") {
      return withCors(await handleLogin(request, env), origin);
    }

    if (request.method === "GET" && url.pathname === "/api/v1/auth/credential-info") {
      return withCors(await handleCredentialInfo(url, env), origin);
    }

    if (request.method === "GET" && url.pathname === "/api/v1/auth/me") {
      return withCors(await handleMe(request, env), origin);
    }

    if (request.method === "POST" && url.pathname === "/api/v1/auth/logout") {
      return withCors(await handleLogout(request, env), origin);
    }

    if (request.method === "POST" && url.pathname === "/api/v1/profile/avatar") {
      return withCors(await handleUpsertAvatar(request, env), origin);
    }

    if (request.method === "GET" && url.pathname === "/api/v1/profile/avatar/by-username") {
      return withCors(await handleGetAvatarByUsername(request, env, url), origin);
    }

    if (request.method === "POST" && url.pathname === "/api/v1/profile/skin") {
      return withCors(await handleUpsertSkinProfile(request, env), origin);
    }

    if (request.method === "GET" && url.pathname === "/api/v1/profile/skin/by-username") {
      return withCors(await handleGetSkinProfileByUsername(url, env), origin);
    }

    if (request.method === "GET" && url.pathname === "/api/v1/profile/skin/by-uuid") {
      return withCors(await handleGetSkinProfileByUuid(url, env), origin);
    }

    if (request.method === "GET" && url.pathname === "/api/v1/profile/skin/image/by-username") {
      return withCors(await handleGetSkinImageByUsername(request, env, url), origin);
    }

    if (request.method === "POST" && url.pathname === "/api/v1/presence/ping") {
      return withCors(await handlePresencePing(request, env), origin);
    }

    if (request.method === "POST" && url.pathname === "/api/v1/relay/session/ensure") {
      return withCors(await handleRelayEnsureSession(request, env), origin);
    }

    if (request.method === "POST" && url.pathname === "/api/v1/relay/session/close") {
      return withCors(await handleRelayCloseSession(request, env), origin);
    }

    if (request.method === "POST" && url.pathname === "/api/v1/relay/connect") {
      return withCors(await handleRelayConnect(request, env), origin);
    }

    if (request.method === "GET" && url.pathname === "/api/v1/relay/host/pending") {
      return withCors(await handleRelayHostPending(request, env), origin);
    }

    if (request.method === "GET" && url.pathname === "/api/v1/relay/ws") {
      return await handleRelayWebSocket(request, env, url);
    }

    if (request.method === "GET" && url.pathname === "/api/v1/friends") {
      return withCors(await handleFriendsList(request, env), origin);
    }

    if (request.method === "POST" && url.pathname === "/api/v1/friends/request") {
      return withCors(await handleSendFriendRequest(request, env), origin);
    }

    if (request.method === "POST" && url.pathname === "/api/v1/friends/respond") {
      return withCors(await handleRespondFriendRequest(request, env), origin);
    }

    if (request.method === "POST" && url.pathname === "/api/v1/friends/remove") {
      return withCors(await handleRemoveFriend(request, env), origin);
    }

    return json({ ok: false, error: "Not found" }, 404, origin);
  } catch (error) {
    return json(
      { ok: false, error: "Internal server error", details: String(error?.message || error) },
      500,
      origin
    );
  }
}

export default {
  async fetch(request, env) {
    return handleFetch(request, env);
  },
};

function createRuntimeEnv(inputEnv = {}) {
  if (inputEnv?.__isRuntimeEnv === true) {
    return inputEnv;
  }

  const hasD1Binding = inputEnv?.DB && typeof inputEnv.DB.prepare === "function";
  if (!hasD1Binding) {
    throw new Error("Cloudflare D1 account storage is not configured. Bind DB and set ACCOUNT_STORE_MODE=d1.");
  }

  const githubRepository = splitGitHubRepository(readEnvString(inputEnv, ["GITHUB_STORAGE_REPOSITORY", "GITHUB_REPOSITORY"]));
  const githubRepoValue = readEnvString(inputEnv, ["GITHUB_STORAGE_REPO", "GITHUB_REPO_NAME"]);
  const githubRepoFromValue = splitGitHubRepository(githubRepoValue);
  const githubStorageOwner =
    readEnvString(inputEnv, ["GITHUB_STORAGE_OWNER", "GITHUB_REPO_OWNER"]) ||
    githubRepoFromValue?.owner ||
    githubRepository?.owner ||
    null;
  const githubStorageRepo = githubRepoFromValue?.repo || githubRepoValue || githubRepository?.repo || null;

  const env = {
    ...(inputEnv || {}),
    __isRuntimeEnv: true,
    RUNTIME_NAME: "cloudflare",
    ACCOUNT_STORE_MODE: "d1",
    DB: inputEnv.DB,
    RELAY_CONNECTIONS: inputEnv.RELAY_CONNECTIONS ?? null,
    PUBLIC_API_BASE_URL: readEnvString(inputEnv, ["PUBLIC_API_BASE_URL"])?.replace(/\/+$/g, "") || null,
    GITHUB_STORAGE_OWNER: githubStorageOwner,
    GITHUB_STORAGE_REPO: githubStorageRepo,
    GITHUB_STORAGE_BRANCH: readEnvString(inputEnv, ["GITHUB_STORAGE_BRANCH", "GITHUB_REPO_BRANCH"], "main"),
    GITHUB_STORAGE_TOKEN: readEnvString(inputEnv, ["GITHUB_STORAGE_TOKEN", "GITHUB_TOKEN"]),
    GITHUB_STORAGE_COMMITTER_NAME: readEnvString(
      inputEnv,
      ["GITHUB_STORAGE_COMMITTER_NAME", "GITHUB_COMMITTER_NAME"],
      "Vesper Launcher"
    ),
    GITHUB_STORAGE_COMMITTER_EMAIL: readEnvString(
      inputEnv,
      ["GITHUB_STORAGE_COMMITTER_EMAIL", "GITHUB_COMMITTER_EMAIL"],
      "vesper-launcher@users.noreply.github.com"
    ),
    AVATAR_STORAGE_MODE: readEnvString(inputEnv, ["AVATAR_STORAGE_MODE"], "d1"),
  };

  return env;
}

function isGitHubAssetStorageEnabled(env) {
  return Boolean(env?.GITHUB_STORAGE_OWNER && env?.GITHUB_STORAGE_REPO && env?.GITHUB_STORAGE_TOKEN);
}

function getAvatarStorageMode(env) {
  const requestedMode =
    typeof env?.AVATAR_STORAGE_MODE === "string" && env.AVATAR_STORAGE_MODE.trim().length > 0
      ? env.AVATAR_STORAGE_MODE.trim().toLowerCase()
      : "d1";

  return requestedMode === "github" && isGitHubAssetStorageEnabled(env)
    ? "github"
    : "d1";
}

function isGitHubAvatarStorageEnabled(env) {
  return getAvatarStorageMode(env) === "github";
}

function getPublicBaseUrl(requestUrl, env) {
  if (typeof env?.PUBLIC_API_BASE_URL === "string" && env.PUBLIC_API_BASE_URL.trim().length > 0) {
    return env.PUBLIC_API_BASE_URL.trim().replace(/\/+$/g, "");
  }

  try {
    return new URL(requestUrl).origin;
  } catch {
    return null;
  }
}

function buildGitHubAvatarPath(username) {
  return `${GITHUB_AVATAR_PATH_PREFIX}/${normalizeUsername(username)}.png`;
}

function buildGitHubSkinPath(username) {
  return `${GITHUB_SKIN_PATH_PREFIX}/${normalizeUsername(username)}.json`;
}

function buildGitHubSkinImagePath(username) {
  return `${GITHUB_SKIN_PATH_PREFIX}/${normalizeUsername(username)}.png`;
}

function buildGitHubContentsUrl(env, storagePath, includeRef = true) {
  const encodedPath = storagePath
    .split("/")
    .map((segment) => encodeURIComponent(segment))
    .join("/");
  const baseUrl = `${GITHUB_API_BASE_URL}/repos/${encodeURIComponent(env.GITHUB_STORAGE_OWNER)}/${encodeURIComponent(
    env.GITHUB_STORAGE_REPO
  )}/contents/${encodedPath}`;
  return includeRef
    ? `${baseUrl}?ref=${encodeURIComponent(env.GITHUB_STORAGE_BRANCH)}`
    : baseUrl;
}

function createGitHubHeaders(env, extraHeaders = {}) {
  return {
    Accept: "application/vnd.github+json",
    Authorization: `Bearer ${env.GITHUB_STORAGE_TOKEN}`,
    "User-Agent": "vesper-account-api",
    "X-GitHub-Api-Version": GITHUB_API_VERSION,
    ...extraHeaders,
  };
}

function bytesToBase64(bytes) {
  let binary = "";
  const chunkSize = 0x8000;
  for (let offset = 0; offset < bytes.length; offset += chunkSize) {
    const chunk = bytes.subarray(offset, Math.min(offset + chunkSize, bytes.length));
    binary += String.fromCharCode(...chunk);
  }

  return btoa(binary);
}

function base64ToBytes(base64) {
  const normalizedBase64 = String(base64 || "").replace(/\s+/g, "");
  return Uint8Array.from(atob(normalizedBase64), (char) => char.charCodeAt(0));
}

function isSafeStoragePath(storagePath) {
  if (typeof storagePath !== "string") {
    return false;
  }
  if (
    storagePath.includes("..") ||
    storagePath.includes("\\") ||
    storagePath.includes("//") ||
    storagePath.startsWith("/")
  ) {
    return false;
  }
  const isAvatar = storagePath.startsWith(`${GITHUB_AVATAR_PATH_PREFIX}/`);
  const isSkin = storagePath.startsWith(`${GITHUB_SKIN_PATH_PREFIX}/`);
  if (!isAvatar && !isSkin) {
    return false;
  }
  const parts = storagePath.split("/");
  if (parts.length !== 3) {
    return false;
  }
  const filename = parts[2];
  const lastDot = filename.lastIndexOf(".");
  if (lastDot === -1) {
    return false;
  }
  const basename = filename.substring(0, lastDot);
  const extension = filename.substring(lastDot).toLowerCase();
  if (isAvatar && extension !== ".png") {
    return false;
  }
  if (isSkin && extension !== ".json" && extension !== ".png") {
    return false;
  }
  return USERNAME_RE.test(basename);
}

async function readGitHubFile(env, storagePath) {
  if (!isGitHubAssetStorageEnabled(env) || !storagePath) {
    return null;
  }

  if (!isSafeStoragePath(storagePath)) {
    console.error(`Security check failed: Unsafe GitHub storage path: ${storagePath}`);
    return null;
  }

  const response = await fetch(buildGitHubContentsUrl(env, storagePath), {
    method: "GET",
    headers: createGitHubHeaders(env),
  });

  if (response.status === 404) {
    return null;
  }

  if (!response.ok) {
    throw new Error(`GitHub read failed for ${storagePath}: ${response.status}`);
  }

  const payload = await response.json();
  const encodedContent =
    typeof payload?.content === "string" && payload.content.trim().length > 0
      ? payload.content.replace(/\n/g, "")
      : null;

  if (!encodedContent) {
    return null;
  }

  return {
    bytes: base64ToBytes(encodedContent),
    sha: typeof payload?.sha === "string" ? payload.sha : null,
  };
}

async function readGitHubJsonFile(env, storagePath) {
  const file = await readGitHubFile(env, storagePath);
  if (!file?.bytes) {
    return null;
  }

  try {
    return JSON.parse(new TextDecoder().decode(file.bytes));
  } catch {
    return null;
  }
}

async function getGitHubFileSha(env, storagePath) {
  const file = await readGitHubFile(env, storagePath);
  return file?.sha || null;
}

async function upsertGitHubFile(env, storagePath, bytes, message) {
  if (!isGitHubAssetStorageEnabled(env)) {
    return null;
  }

  if (!isSafeStoragePath(storagePath)) {
    throw new Error(`Security check failed: Unsafe GitHub storage path: ${storagePath}`);
  }

  const existingSha = await getGitHubFileSha(env, storagePath);
  const body = {
    message,
    content: bytesToBase64(bytes),
    branch: env.GITHUB_STORAGE_BRANCH,
    committer: {
      name: env.GITHUB_STORAGE_COMMITTER_NAME,
      email: env.GITHUB_STORAGE_COMMITTER_EMAIL,
    },
    ...(existingSha ? { sha: existingSha } : {}),
  };

  const response = await fetch(buildGitHubContentsUrl(env, storagePath, false), {
    method: "PUT",
    headers: createGitHubHeaders(env, {
      "Content-Type": "application/json; charset=utf-8",
    }),
    body: JSON.stringify(body),
  });

  if (!response.ok) {
    throw new Error(`GitHub write failed for ${storagePath}: ${response.status}`);
  }

  const payload = await response.json();
  return {
    sha: typeof payload?.content?.sha === "string" ? payload.content.sha : existingSha,
  };
}

async function deleteGitHubFile(env, storagePath, sha, message) {
  if (!isGitHubAssetStorageEnabled(env) || !storagePath) {
    return;
  }

  if (!isSafeStoragePath(storagePath)) {
    throw new Error(`Security check failed: Unsafe GitHub storage path: ${storagePath}`);
  }

  const resolvedSha = sha || (await getGitHubFileSha(env, storagePath));
  if (!resolvedSha) {
    return;
  }

  const response = await fetch(buildGitHubContentsUrl(env, storagePath, false), {
    method: "DELETE",
    headers: createGitHubHeaders(env, {
      "Content-Type": "application/json; charset=utf-8",
    }),
    body: JSON.stringify({
      message,
      sha: resolvedSha,
      branch: env.GITHUB_STORAGE_BRANCH,
      committer: {
        name: env.GITHUB_STORAGE_COMMITTER_NAME,
        email: env.GITHUB_STORAGE_COMMITTER_EMAIL,
      },
    }),
  });

  if (response.status === 404) {
    return;
  }

  if (!response.ok) {
    throw new Error(`GitHub delete failed for ${storagePath}: ${response.status}`);
  }
}

function buildAvatarPublicUrl(baseUrl, username, updatedAtUtc) {
  if (!baseUrl || !username) {
    return null;
  }

  const url = new URL("/api/v1/profile/avatar/by-username", `${baseUrl}/`);
  url.searchParams.set("username", username);
  if (typeof updatedAtUtc === "string" && updatedAtUtc.trim().length > 0) {
    url.searchParams.set("v", updatedAtUtc.trim());
  }

  return url.toString();
}

function buildSkinPublicUrl(baseUrl, username, updatedAtUtc) {
  if (!baseUrl || !username) {
    return null;
  }

  const url = new URL("/api/v1/profile/skin/image/by-username", `${baseUrl}/`);
  url.searchParams.set("username", username);
  if (typeof updatedAtUtc === "string" && updatedAtUtc.trim().length > 0) {
    url.searchParams.set("v", updatedAtUtc.trim());
  }

  return url.toString();
}

async function lookupUserByUsername(env, username) {
  const normalizedUsername = normalizeUsername(username);
  if (!normalizedUsername) {
    return null;
  }

  const row = await env.DB.prepare(
    `SELECT id, username
       FROM users
      WHERE username = ? COLLATE NOCASE
      LIMIT 1`
  )
    .bind(normalizedUsername)
    .first();

  if (!row) {
    return null;
  }

  return {
    userId: Number(row.id),
    username: String(row.username),
    source: "db",
  };
}

async function getDisplayUsernameForUserId(env, userId, fallbackUsername = null) {
  if (typeof fallbackUsername === "string" && fallbackUsername.trim().length > 0) {
    return fallbackUsername.trim();
  }

  return null;
}

async function handleRegister(request, env) {
  const body = await readJson(request);
  const username = normalizeUsername(body?.username);
  if (!username) {
    return json({ ok: false, error: "Invalid username" }, 400);
  }

  const existing = await lookupUserByUsername(env, username);
  if (existing) {
    return json({ ok: false, error: "Username already exists" }, 409);
  }

  let passwordHashHex = null;
  let passwordSaltHex = null;
  let passwordAlgorithm = "PBKDF2-SHA256";
  let passwordIterations = DEFAULT_PASSWORD_ITERATIONS;

  if (typeof body?.password === "string" && body.password.length >= 6) {
    const salt = crypto.getRandomValues(new Uint8Array(16));
    const hash = await derivePbkdf2Sha256(body.password, salt, DEFAULT_PASSWORD_ITERATIONS, 32);
    passwordHashHex = bytesToHex(hash);
    passwordSaltHex = bytesToHex(salt);
  } else if (
    typeof body?.passwordHash === "string" &&
    typeof body?.passwordSalt === "string" &&
    isHex(body.passwordHash) &&
    isHex(body.passwordSalt)
  ) {
    passwordHashHex = body.passwordHash.toLowerCase();
    passwordSaltHex = body.passwordSalt.toLowerCase();
    passwordAlgorithm =
      typeof body?.passwordAlgorithm === "string" && body.passwordAlgorithm.trim().length > 0
        ? body.passwordAlgorithm.trim()
        : "PBKDF2-SHA256";
    const parsedIterations = Number(body?.passwordIterations);
    if (Number.isFinite(parsedIterations) && parsedIterations > 0) {
      passwordIterations = Math.trunc(parsedIterations);
    }
  } else {
    return json(
      {
        ok: false,
        error:
          "Password is required. Send either { password } or legacy fields { passwordHash, passwordSalt }.",
      },
      400
    );
  }

  const now = new Date().toISOString();
  const provisionalStoredUsername = username;
  const insertResult = await env.DB.prepare(
    `INSERT INTO users
      (username, password_hash, password_salt, password_algorithm, password_iterations, created_at_utc, updated_at_utc)
      VALUES (?, ?, ?, ?, ?, ?, ?)`
  )
    .bind(
      provisionalStoredUsername,
      passwordHashHex,
      passwordSaltHex,
      passwordAlgorithm,
      passwordIterations,
      now,
      now
    )
    .run();

  const userId = Number(insertResult.meta?.last_row_id);
  if (!Number.isFinite(userId) || userId <= 0) {
    return json({ ok: false, error: "Failed to create account" }, 500);
  }

  const session = await createSession(env, userId, request.headers.get("User-Agent"));
  return json(
    {
      ok: true,
      user: { id: userId, username },
      accessToken: session.token,
      expiresAtUtc: session.expiresAtUtc,
    },
    201
  );
}

async function handleLogin(request, env) {
  const body = await readJson(request);
  const username = normalizeUsername(body?.username);
  const password = typeof body?.password === "string" ? body.password : "";
  const incomingPasswordHashHex =
    typeof body?.passwordHash === "string" && isHex(body.passwordHash) ? body.passwordHash.toLowerCase() : null;
  if (!username || (password.length < 1 && !incomingPasswordHashHex)) {
    return json({ ok: false, error: "Username and password are required" }, 400);
  }

  const resolvedUser = await lookupUserByUsername(env, username);
  if (!resolvedUser?.userId) {
    return json({ ok: false, error: "Invalid credentials" }, 401);
  }

  const row = await env.DB.prepare(
    `SELECT id, username, password_hash, password_salt, password_algorithm, password_iterations
       FROM users
      WHERE id = ?
      LIMIT 1`
  )
    .bind(resolvedUser.userId)
    .first();

  if (!row) {
    return json({ ok: false, error: "Invalid credentials" }, 401);
  }

  const algorithm = String(row.password_algorithm || "").trim().toUpperCase();
  if (algorithm !== "PBKDF2-SHA256") {
    return json({ ok: false, error: "Unsupported password algorithm" }, 401);
  }

  if (!isHex(row.password_hash) || !isHex(row.password_salt)) {
    return json({ ok: false, error: "Stored credential format is invalid" }, 500);
  }

  const storedHash = hexToBytes(row.password_hash);
  const computedHash = incomingPasswordHashHex
    ? hexToBytes(incomingPasswordHashHex)
    : await computePasswordHashForStoredCredentials(password, row.password_salt, row.password_iterations, storedHash.length);
  if (!timingSafeEqual(storedHash, computedHash)) {
    return json({ ok: false, error: "Invalid credentials" }, 401);
  }

  const session = await createSession(env, Number(row.id), request.headers.get("User-Agent"));
  return json({
    ok: true,
    user: { id: Number(row.id), username: resolvedUser.username },
    accessToken: session.token,
    expiresAtUtc: session.expiresAtUtc,
  });
}

async function handleCredentialInfo(url, env) {
  const username = normalizeUsername(url.searchParams.get("username"));
  if (!username) {
    return json({ ok: false, error: "Invalid username" }, 400);
  }

  const resolvedUser = await lookupUserByUsername(env, username);
  if (!resolvedUser?.userId) {
    return json({ ok: false, error: "Account not found" }, 404);
  }

  const row = await env.DB.prepare(
    `SELECT username, password_salt, password_algorithm, password_iterations
       FROM users
       WHERE id = ?
       LIMIT 1`
  )
    .bind(resolvedUser.userId)
    .first();

  if (!row) {
    return json({ ok: false, error: "Account not found" }, 404);
  }

  return json({
    ok: true,
    user: { username: resolvedUser.username },
    passwordSalt: String(row.password_salt || ""),
    passwordAlgorithm: String(row.password_algorithm || "PBKDF2-SHA256"),
    passwordIterations:
      Number(row.password_iterations) > 0 ? Number(row.password_iterations) : DEFAULT_PASSWORD_ITERATIONS,
  });
}

async function handleMe(request, env) {
  const auth = await authenticate(request, env);
  if (!auth.ok) {
    return json({ ok: false, error: auth.error }, 401);
  }

  const avatar = await getUserAvatar(env, auth.userId, auth.username, getPublicBaseUrl(request.url, env));

  return json({
    ok: true,
    user: {
      id: auth.userId,
      username: auth.username,
      sessionExpiresAtUtc: auth.expiresAtUtc,
    },
    avatar,
  });
}

async function handleLogout(request, env) {
  const auth = await authenticate(request, env);
  if (!auth.ok) {
    return json({ ok: false, error: auth.error }, 401);
  }

  await env.DB.prepare("DELETE FROM sessions WHERE id = ?").bind(auth.sessionId).run();
  return json({ ok: true });
}

async function handleUpsertAvatar(request, env) {
  const auth = await authenticate(request, env);
  if (!auth.ok) {
    return json({ ok: false, error: auth.error }, 401);
  }

  const body = await readJson(request);
  const removeAvatar = body?.remove === true;
  const imageBase64 = typeof body?.imageBase64 === "string" ? body.imageBase64.trim() : "";
  const contentType = typeof body?.contentType === "string" ? body.contentType.trim().toLowerCase() : "";

  if (removeAvatar) {
    const existingAvatar = await env.DB.prepare(
      `SELECT storage_provider, storage_path, storage_sha
         FROM user_avatars
        WHERE user_id = ?
        LIMIT 1`
    )
      .bind(auth.userId)
      .first();

    if (
      String(existingAvatar?.storage_provider || "").trim().toLowerCase() === "github" &&
      typeof existingAvatar?.storage_path === "string" &&
      existingAvatar.storage_path.trim().length > 0
    ) {
      await deleteGitHubFile(
        env,
        String(existingAvatar.storage_path),
        typeof existingAvatar?.storage_sha === "string" ? String(existingAvatar.storage_sha) : null,
        `Remove avatar for ${auth.username}`
      );
    }

    await env.DB.prepare("DELETE FROM user_avatars WHERE user_id = ?").bind(auth.userId).run();
    await env.DB.prepare("UPDATE users SET updated_at_utc = ? WHERE id = ?")
      .bind(new Date().toISOString(), auth.userId)
      .run();
    return json({ ok: true, removed: true, avatar: null });
  }

  if (!imageBase64 || !contentType) {
    return json({ ok: false, error: "Avatar image is required" }, 400);
  }

  if (!/^image\/(png|jpeg|jpg|webp|bmp)$/i.test(contentType)) {
    return json({ ok: false, error: "Unsupported avatar format" }, 400);
  }

  let avatarByteLength = 0;
  let decodedAvatarBytes = null;
  try {
    decodedAvatarBytes = base64ToBytes(imageBase64);
    avatarByteLength = decodedAvatarBytes.byteLength;
    if (avatarByteLength <= 0) {
      return json({ ok: false, error: "Avatar image is required" }, 400);
    }

    if (avatarByteLength > MAX_AVATAR_BYTES) {
      return json({ ok: false, error: "Avatar is too large" }, 413);
    }
  } catch {
    return json({ ok: false, error: "Invalid avatar payload" }, 400);
  }

  const now = new Date().toISOString();
  let storageProvider = null;
  let storagePath = null;
  let storageSha = null;
  let storedImageBase64 = imageBase64.replace(/\s+/g, "");

  if (decodedAvatarBytes && isGitHubAvatarStorageEnabled(env)) {
    try {
      storagePath = buildGitHubAvatarPath(auth.username);
      const githubWriteResult = await upsertGitHubFile(
        env,
        storagePath,
        decodedAvatarBytes,
        `Upsert avatar for ${auth.username}`
      );
      storageProvider = "github";
      storageSha = githubWriteResult?.sha || null;
      storedImageBase64 = "";
    } catch (error) {
      console.error(`GitHub avatar storage failed, falling back to D1: ${String(error?.message || error)}`);
      storageProvider = null;
      storagePath = null;
      storageSha = null;
      storedImageBase64 = imageBase64.replace(/\s+/g, "");
    }
  }

  await env.DB.prepare(
    `INSERT INTO user_avatars
      (user_id, content_type, image_base64, byte_length, updated_at_utc, storage_provider, storage_path, storage_sha)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?)
      ON CONFLICT(user_id) DO UPDATE SET
        content_type = excluded.content_type,
        image_base64 = excluded.image_base64,
        byte_length = excluded.byte_length,
        updated_at_utc = excluded.updated_at_utc,
        storage_provider = excluded.storage_provider,
        storage_path = excluded.storage_path,
        storage_sha = excluded.storage_sha`
  )
    .bind(auth.userId, contentType, storedImageBase64, avatarByteLength, now, storageProvider, storagePath, storageSha)
    .run();

  await env.DB.prepare("UPDATE users SET updated_at_utc = ? WHERE id = ?")
    .bind(now, auth.userId)
    .run();

  return json({
    ok: true,
    avatar: buildAvatarPayload(
      {
        content_type: contentType,
        image_base64: storedImageBase64,
        byte_length: avatarByteLength,
        updated_at_utc: now,
        storage_provider: storageProvider,
      },
      auth.username,
      getPublicBaseUrl(request.url, env)
    ),
  });
}

async function handleGetAvatarByUsername(request, env, url) {
  const username = normalizeUsername(url.searchParams.get("username"));
  if (!username) {
    return json({ ok: false, error: "Invalid username" }, 400);
  }

  const resolvedUser = await lookupUserByUsername(env, username);
  if (!resolvedUser?.userId) {
    return json({ ok: false, error: "Account not found" }, 404);
  }

  const row = await env.DB.prepare(
    `SELECT content_type, image_base64, byte_length, updated_at_utc, storage_provider, storage_path, storage_sha
       FROM user_avatars
      WHERE user_id = ?
      LIMIT 1`
  )
    .bind(resolvedUser.userId)
    .first();

  if (!row) {
    return json({ ok: false, error: "Avatar not found" }, 404);
  }

  let avatarBytes = null;
  if (
    String(row.storage_provider || "").trim().toLowerCase() === "github" &&
    typeof row.storage_path === "string" &&
    row.storage_path.trim().length > 0
  ) {
    try {
      const githubFile = await readGitHubFile(env, String(row.storage_path));
      avatarBytes = githubFile?.bytes || null;
    } catch (error) {
      console.error(`GitHub avatar read failed, falling back to D1 payload: ${String(error?.message || error)}`);
    }

    if ((!avatarBytes || avatarBytes.byteLength <= 0) &&
        typeof row.image_base64 === "string" &&
        row.image_base64.trim().length > 0) {
      avatarBytes = base64ToBytes(row.image_base64);
    }
  } else if (typeof row.image_base64 === "string" && row.image_base64.trim().length > 0) {
    avatarBytes = base64ToBytes(row.image_base64);
  }

  if (!avatarBytes || avatarBytes.byteLength <= 0) {
    return json({ ok: false, error: "Avatar not found" }, 404);
  }

  const response = new Response(avatarBytes, {
    status: 200,
    headers: {
      "Content-Type": typeof row.content_type === "string" && row.content_type.trim().length > 0 ? String(row.content_type) : "image/png",
      "Cache-Control": "public, max-age=900, s-maxage=86400",
    },
  });

  if (typeof row.updated_at_utc === "string" && row.updated_at_utc.trim().length > 0) {
    response.headers.set("ETag", `"${String(row.updated_at_utc).trim()}"`);
  }

  return response;
}

async function handleUpsertSkinProfile(request, env) {
  const auth = await authenticate(request, env);
  if (!auth.ok) {
    return json({ ok: false, error: auth.error }, 401);
  }

  const body = await readJson(request);
  const removeSkin = body?.remove === true;
  if (removeSkin) {
    const existingSkinProfile = await env.DB.prepare(
      `SELECT storage_provider, storage_path, storage_sha
         FROM user_skin_profiles
        WHERE user_id = ?
        LIMIT 1`
    )
      .bind(auth.userId)
      .first();

    if (
      String(existingSkinProfile?.storage_provider || "").trim().toLowerCase() === "github" &&
      typeof existingSkinProfile?.storage_path === "string" &&
      existingSkinProfile.storage_path.trim().length > 0
    ) {
      await deleteGitHubFile(
        env,
        String(existingSkinProfile.storage_path),
        typeof existingSkinProfile?.storage_sha === "string" ? String(existingSkinProfile.storage_sha) : null,
        `Remove skin manifest for ${auth.username}`
      );
      await deleteGitHubFile(
        env,
        buildGitHubSkinImagePath(auth.username),
        null,
        `Remove skin image for ${auth.username}`
      );
    }

    await env.DB.prepare("DELETE FROM user_skin_profiles WHERE user_id = ?").bind(auth.userId).run();
    await env.DB.prepare("UPDATE users SET updated_at_utc = ? WHERE id = ?")
      .bind(new Date().toISOString(), auth.userId)
      .run();
    return json({ ok: true, removed: true, skin: null });
  }

  const publishedUuid = normalizeUuid(body?.publishedUuid ?? body?.uuid);
  const offlineUuid = normalizeUuid(body?.offlineUuid);
  const textureValue = normalizeRequiredText(body?.textureValue, 12000);
  const textureSignature = normalizeOptionalText(body?.textureSignature, 12000);
  const textureUrl = normalizeUrlText(body?.textureUrl, 1000);
  const imageBase64 = typeof body?.imageBase64 === "string" ? body.imageBase64.trim() : "";
  const imageContentType = typeof body?.imageContentType === "string" ? body.imageContentType.trim().toLowerCase() : "";
  if (!publishedUuid || !offlineUuid || !textureValue) {
    return json({ ok: false, error: "Skin texture payload is invalid" }, 400);
  }

  let decodedImageBytes = null;
  if (imageBase64.length > 0) {
    if (!/^image\/png$/i.test(imageContentType || "image/png")) {
      return json({ ok: false, error: "Unsupported skin image format" }, 400);
    }

    try {
      decodedImageBytes = base64ToBytes(imageBase64);
      if (!decodedImageBytes || decodedImageBytes.byteLength <= 0) {
        return json({ ok: false, error: "Skin image is required" }, 400);
      }

      if (decodedImageBytes.byteLength > 1024 * 1024) {
        return json({ ok: false, error: "Skin image is too large" }, 413);
      }
    } catch {
      return json({ ok: false, error: "Invalid skin image payload" }, 400);
    }
  }

  const now = new Date().toISOString();
  let storageProvider = null;
  let storagePath = null;
  let storageSha = null;
  let effectiveTextureUrl = textureUrl;

  if (isGitHubAssetStorageEnabled(env)) {
    if (decodedImageBytes) {
      await upsertGitHubFile(
        env,
        buildGitHubSkinImagePath(auth.username),
        decodedImageBytes,
        `Upsert skin image for ${auth.username}`
      );
      effectiveTextureUrl = buildSkinPublicUrl(getPublicBaseUrl(request.url, env), auth.username, now);
    }

    storagePath = buildGitHubSkinPath(auth.username);
    const manifestBytes = encoder.encode(
      JSON.stringify(
        {
          username: auth.username,
          uuid: publishedUuid,
          offlineUuid,
          textureValue,
          textureSignature,
          textureUrl: effectiveTextureUrl,
          imagePath: decodedImageBytes ? buildGitHubSkinImagePath(auth.username) : null,
          updatedAtUtc: now,
        },
        null,
        2
      )
    );
    const githubWriteResult = await upsertGitHubFile(
      env,
      storagePath,
      manifestBytes,
      `Upsert skin manifest for ${auth.username}`
    );
    storageProvider = "github";
    storageSha = githubWriteResult?.sha || null;
  }

  await env.DB.prepare(
    `INSERT INTO user_skin_profiles
      (user_id, published_uuid, offline_uuid, texture_value, texture_signature, texture_url, updated_at_utc, storage_provider, storage_path, storage_sha)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
      ON CONFLICT(user_id) DO UPDATE SET
        published_uuid = excluded.published_uuid,
        offline_uuid = excluded.offline_uuid,
        texture_value = excluded.texture_value,
        texture_signature = excluded.texture_signature,
        texture_url = excluded.texture_url,
        updated_at_utc = excluded.updated_at_utc,
        storage_provider = excluded.storage_provider,
        storage_path = excluded.storage_path,
        storage_sha = excluded.storage_sha`
  )
    .bind(
      auth.userId,
      publishedUuid,
      offlineUuid,
      textureValue,
      textureSignature,
      effectiveTextureUrl,
      now,
      storageProvider,
      storagePath,
      storageSha
    )
    .run();

  await env.DB.prepare("UPDATE users SET updated_at_utc = ? WHERE id = ?")
    .bind(now, auth.userId)
    .run();

  return json({
    ok: true,
    skin: {
      username: auth.username,
      uuid: publishedUuid,
      offlineUuid,
      textureValue,
      textureSignature,
      textureUrl: effectiveTextureUrl,
      updatedAtUtc: now,
    },
  });
}

async function handleGetSkinProfileByUsername(url, env) {
  const username = normalizeUsername(url.searchParams.get("username"));
  if (!username) {
    return json({ ok: false, error: "Invalid username" }, 400);
  }

  const row = await env.DB.prepare(
    `SELECT u.username,
            usp.published_uuid,
            usp.offline_uuid,
            usp.texture_value,
            usp.texture_signature,
            usp.texture_url,
            usp.storage_provider,
            usp.storage_path,
            usp.storage_sha,
            usp.updated_at_utc
       FROM user_skin_profiles usp
       JOIN users u ON u.id = usp.user_id
      WHERE u.username = ? COLLATE NOCASE
      LIMIT 1`
  )
    .bind(username)
    .first();

  if (!row) {
    return json({ ok: false, error: "Skin not found" }, 404);
  }

  return json({ ok: true, entry: await buildSkinProfilePayload(env, row) });
}

async function handleGetSkinProfileByUuid(url, env) {
  const uuid = normalizeUuid(url.searchParams.get("uuid"));
  if (!uuid) {
    return json({ ok: false, error: "Invalid uuid" }, 400);
  }

  const row = await env.DB.prepare(
    `SELECT u.username,
            usp.published_uuid,
            usp.offline_uuid,
            usp.texture_value,
            usp.texture_signature,
            usp.texture_url,
            usp.storage_provider,
            usp.storage_path,
            usp.storage_sha,
            usp.updated_at_utc
       FROM user_skin_profiles usp
       JOIN users u ON u.id = usp.user_id
      WHERE usp.published_uuid = ?
         OR usp.offline_uuid = ?
      LIMIT 1`
  )
    .bind(uuid, uuid)
    .first();

  if (!row) {
    return json({ ok: false, error: "Skin not found" }, 404);
  }

  return json({ ok: true, entry: await buildSkinProfilePayload(env, row) });
}

async function handleGetSkinImageByUsername(request, env, url) {
  const username = normalizeUsername(url.searchParams.get("username"));
  if (!username) {
    return json({ ok: false, error: "Invalid username" }, 400);
  }

  const resolvedUser = await lookupUserByUsername(env, username);
  if (!resolvedUser?.userId) {
    return json({ ok: false, error: "Account not found" }, 404);
  }

  const row = await env.DB.prepare(
    `SELECT updated_at_utc, storage_provider, storage_path
       FROM user_skin_profiles
      WHERE user_id = ?
      LIMIT 1`
  )
    .bind(resolvedUser.userId)
    .first();

  if (!row) {
    return json({ ok: false, error: "Skin not found" }, 404);
  }

  let skinBytes = null;
  if (
    String(row.storage_provider || "").trim().toLowerCase() === "github"
  ) {
    const manifestImagePath = buildGitHubSkinImagePath(resolvedUser.username);
    const githubFile = await readGitHubFile(env, manifestImagePath);
    skinBytes = githubFile?.bytes || null;
  }

  if (!skinBytes || skinBytes.byteLength <= 0) {
    return json({ ok: false, error: "Skin image not found" }, 404);
  }

  const response = new Response(skinBytes, {
    status: 200,
    headers: {
      "Content-Type": "image/png",
      "Cache-Control": "public, max-age=900, s-maxage=86400",
    },
  });

  if (typeof row.updated_at_utc === "string" && row.updated_at_utc.trim().length > 0) {
    response.headers.set("ETag", `"${String(row.updated_at_utc).trim()}"`);
  }

  return response;
}

async function handlePresencePing(request, env) {
  const auth = await authenticate(request, env);
  if (!auth.ok) {
    return json({ ok: false, error: auth.error }, 401);
  }

  const body = await readJson(request);
  const activityKind = normalizeActivityKind(body?.activityKind);
  const activityName = normalizeOptionalText(body?.activityName, 120);
  const versionId = normalizeOptionalText(body?.versionId, 80);
  const joinHost = normalizeOptionalText(body?.joinHost, 120);
  const joinPort = normalizeJoinPort(body?.joinPort);
  const relayRoomId = normalizeRelayRoomId(body?.relayRoomId);
  const relayTransportMode = normalizeRelayTransportMode(body?.relayTransportMode);
  const hasDirectJoinEndpoint = !!joinHost && Number.isFinite(joinPort);
  const isJoinable = Boolean(body?.isJoinable) && (hasDirectJoinEndpoint || !!relayRoomId);
  const now = new Date().toISOString();
  await env.DB.prepare(
    `INSERT INTO user_presence
      (user_id, last_ping_at_utc, updated_at_utc)
      VALUES (?, ?, ?)
      ON CONFLICT(user_id) DO UPDATE SET
        last_ping_at_utc = excluded.last_ping_at_utc,
        updated_at_utc = excluded.updated_at_utc`
  )
    .bind(auth.userId, now, now)
    .run();

  await env.DB.prepare(
    `INSERT INTO user_game_activity
      (user_id, activity_kind, activity_name, version_id, join_host, join_port, relay_room_id, relay_transport_mode, is_joinable, updated_at_utc)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
      ON CONFLICT(user_id) DO UPDATE SET
        activity_kind = excluded.activity_kind,
        activity_name = excluded.activity_name,
        version_id = excluded.version_id,
        join_host = excluded.join_host,
        join_port = excluded.join_port,
        relay_room_id = excluded.relay_room_id,
        relay_transport_mode = excluded.relay_transport_mode,
        is_joinable = excluded.is_joinable,
        updated_at_utc = excluded.updated_at_utc`
  )
    .bind(
      auth.userId,
      activityKind,
      activityName,
      versionId,
      joinHost,
      joinPort,
      relayRoomId,
      relayTransportMode,
      isJoinable ? 1 : 0,
      now
    )
    .run();

  return json({
    ok: true,
    user: { id: auth.userId, username: auth.username },
    lastPingAtUtc: now,
    activity: {
      kind: activityKind,
      name: activityName,
      versionId,
      joinHost,
      joinPort,
      relayRoomId,
      relayTransportMode,
      isJoinable,
    },
  });
}

async function handleRelayEnsureSession(request, env) {
  const auth = await authenticate(request, env);
  if (!auth.ok) {
    return json({ ok: false, error: auth.error }, 401);
  }

  const now = new Date().toISOString();
  const existing = await env.DB.prepare(
    `SELECT room_id
       FROM relay_sessions
      WHERE host_user_id = ?
      LIMIT 1`
  )
    .bind(auth.userId)
    .first();

  const roomId = normalizeRelayRoomId(existing?.room_id) || createRelayRoomId();
  await env.DB.prepare(
    `INSERT INTO relay_sessions
      (room_id, host_user_id, transport_mode, is_active, created_at_utc, updated_at_utc)
      VALUES (?, ?, ?, 1, ?, ?)
      ON CONFLICT(room_id) DO UPDATE SET
        host_user_id = excluded.host_user_id,
        transport_mode = excluded.transport_mode,
        is_active = 1,
        updated_at_utc = excluded.updated_at_utc`
  )
    .bind(roomId, auth.userId, RELAY_TRANSPORT_MODE, now, now)
    .run();

  return json({
    ok: true,
    roomId,
    transportMode: RELAY_TRANSPORT_MODE,
  });
}

async function handleRelayCloseSession(request, env) {
  const auth = await authenticate(request, env);
  if (!auth.ok) {
    return json({ ok: false, error: auth.error }, 401);
  }

  const now = new Date().toISOString();
  await env.DB.prepare(
    `UPDATE relay_sessions
        SET is_active = 0,
            updated_at_utc = ?
      WHERE host_user_id = ?`
  )
    .bind(now, auth.userId)
    .run();

  await env.DB.prepare(
    `UPDATE relay_connections
        SET status = 'closed',
            updated_at_utc = ?,
            closed_at_utc = COALESCE(closed_at_utc, ?)
      WHERE host_user_id = ?
        AND status != 'closed'`
  )
    .bind(now, now, auth.userId)
    .run();

  return json({ ok: true });
}

async function handleRelayConnect(request, env) {
  const auth = await authenticate(request, env);
  if (!auth.ok) {
    return json({ ok: false, error: auth.error }, 401);
  }

  const body = await readJson(request);
  const roomId = normalizeRelayRoomId(body?.roomId);
  if (!roomId) {
    return json({ ok: false, error: "Invalid relay room id" }, 400);
  }

  const relaySession = await resolveActiveRelaySessionByRoomId(env, roomId);
  if (!relaySession) {
    return json({ ok: false, error: "Relay session is offline" }, 404);
  }

  if (relaySession.hostUserId === auth.userId) {
    return json({ ok: false, error: "Cannot connect to your own relay session" }, 400);
  }

  const friends = await areFriends(env, auth.userId, relaySession.hostUserId);
  if (!friends) {
    return json({ ok: false, error: "Relay session is available to friends only" }, 403);
  }

  await cleanupExpiredRelayConnections(env, relaySession.hostUserId);

  const now = new Date().toISOString();
  const connectionId = createRelayConnectionId();
  await env.DB.prepare(
    `INSERT INTO relay_connections
      (connection_id, room_id, host_user_id, guest_user_id, status, created_at_utc, updated_at_utc)
      VALUES (?, ?, ?, ?, 'pending', ?, ?)`
  )
    .bind(connectionId, roomId, relaySession.hostUserId, auth.userId, now, now)
    .run();

  return json({
    ok: true,
    roomId,
    connectionId,
    transportMode: RELAY_TRANSPORT_MODE,
    webSocketUrl: buildRelayWebSocketUrl(request.url, connectionId, "guest"),
  });
}

async function handleRelayHostPending(request, env) {
  const auth = await authenticate(request, env);
  if (!auth.ok) {
    return json({ ok: false, error: auth.error }, 401);
  }

  const relaySession = await resolveActiveRelaySessionForUserId(env, auth.userId);
  if (!relaySession) {
    return json({ ok: true, connections: [] });
  }

  await cleanupExpiredRelayConnections(env, auth.userId);

  const rows = await env.DB.prepare(
    `SELECT rc.connection_id, rc.room_id, rc.created_at_utc, u.username AS guest_username
       FROM relay_connections rc
       JOIN users u ON u.id = rc.guest_user_id
      WHERE rc.host_user_id = ?
        AND rc.status = 'pending'
      ORDER BY rc.created_at_utc ASC
      LIMIT 12`
  )
    .bind(auth.userId)
    .all();

  return json({
    ok: true,
    roomId: relaySession.roomId,
    transportMode: relaySession.transportMode,
    connections: (rows.results || []).map((row) => ({
      connectionId: String(row.connection_id),
      roomId: String(row.room_id),
      guestUsername: typeof row.guest_username === "string" ? String(row.guest_username) : null,
      createdAtUtc: typeof row.created_at_utc === "string" ? String(row.created_at_utc) : null,
      webSocketUrl: buildRelayWebSocketUrl(request.url, String(row.connection_id), "host"),
    })),
  });
}

async function handleRelayWebSocket(request, env, url) {
  if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
    return json({ ok: false, error: "Expected websocket upgrade" }, 426);
  }

  if (!env.RELAY_CONNECTIONS || typeof env.RELAY_CONNECTIONS.get !== "function") {
    return json({ ok: false, error: "Relay websocket binding is not configured" }, 500);
  }

  const auth = await authenticate(request, env);
  if (!auth.ok) {
    return json({ ok: false, error: auth.error }, 401);
  }

  const connectionId = normalizeRelayConnectionId(url.searchParams.get("connectionId"));
  const role = normalizeRelaySocketRole(url.searchParams.get("role"));
  if (!connectionId || !role) {
    return json({ ok: false, error: "Invalid relay websocket arguments" }, 400);
  }

  const row = await env.DB.prepare(
    `SELECT connection_id, host_user_id, guest_user_id, status, created_at_utc
       FROM relay_connections
      WHERE connection_id = ?
      LIMIT 1`
  )
    .bind(connectionId)
    .first();

  if (!row) {
    return json({ ok: false, error: "Relay connection not found" }, 404);
  }

  const createdAt = Date.parse(String(row.created_at_utc || ""));
  if (!Number.isFinite(createdAt) || createdAt + RELAY_CONNECTION_TTL_MS <= Date.now()) {
    return json({ ok: false, error: "Relay connection expired" }, 410);
  }

  const expectedUserId = role === "host" ? Number(row.host_user_id) : Number(row.guest_user_id);
  if (expectedUserId !== auth.userId) {
    return json({ ok: false, error: "Relay connection access denied" }, 403);
  }

  const now = new Date().toISOString();
  await env.DB.prepare(
    `UPDATE relay_connections
        SET status = CASE
              WHEN status = 'pending' THEN 'bridging'
              ELSE status
            END,
            updated_at_utc = ?
      WHERE connection_id = ?`
  )
    .bind(now, connectionId)
    .run();

  const relayId = env.RELAY_CONNECTIONS.idFromName(connectionId);
  const stub = env.RELAY_CONNECTIONS.get(relayId);
  const relayRequest = new Request(`https://relay.internal/ws?connectionId=${encodeURIComponent(connectionId)}`, {
    method: "GET",
    headers: {
      Upgrade: "websocket",
      "X-Vesper-Relay-Role": role,
    },
  });

  return stub.fetch(relayRequest);
}

async function handleFriendsList(request, env) {
  const auth = await authenticate(request, env);
  if (!auth.ok) {
    return json({ ok: false, error: auth.error }, 401);
  }

  const nowIso = new Date().toISOString();
  const onlineThresholdIso = new Date(Date.now() - ONLINE_ACTIVITY_WINDOW_MS).toISOString();
  const publicBaseUrl = getPublicBaseUrl(request.url, env);

  const friendsResult = await env.DB.prepare(
    `SELECT u.id AS friend_user_id,
            u.username AS db_username,
            ua.content_type AS avatar_content_type,
            ua.image_base64 AS avatar_image_base64,
            ua.byte_length AS avatar_byte_length,
            ua.updated_at_utc AS avatar_updated_at_utc,
            ua.storage_provider AS avatar_storage_provider,
            ua.storage_path AS avatar_storage_path,
            ua.storage_sha AS avatar_storage_sha,
            uga.activity_kind,
            uga.activity_name,
            uga.version_id,
            uga.join_host,
            uga.join_port,
            uga.relay_room_id,
            uga.relay_transport_mode,
            uga.is_joinable,
            (
              SELECT MAX(s.last_seen_at_utc)
                FROM sessions s
               WHERE s.user_id = u.id
                 AND s.expires_at_utc > ?
            ) AS session_last_seen_at_utc,
            CASE
              WHEN up.last_ping_at_utc IS NOT NULL
                   AND up.last_ping_at_utc >= ?
              THEN 1
              WHEN (
                SELECT MAX(s.last_seen_at_utc)
                  FROM sessions s
                 WHERE s.user_id = u.id
                   AND s.expires_at_utc > ?
              ) >= ?
              THEN 1
              ELSE 0
            END AS is_online,
            up.last_ping_at_utc AS last_seen_at_utc
       FROM friendships f
       JOIN users u ON u.id = CASE
           WHEN f.user_low_id = ? THEN f.user_high_id
           ELSE f.user_low_id
         END
       LEFT JOIN user_avatars ua ON ua.user_id = u.id
       LEFT JOIN user_presence up ON up.user_id = u.id
       LEFT JOIN user_game_activity uga ON uga.user_id = u.id
      WHERE f.user_low_id = ? OR f.user_high_id = ?`
  )
    .bind(nowIso, onlineThresholdIso, nowIso, onlineThresholdIso, auth.userId, auth.userId, auth.userId)
    .all();

  const incomingResult = await env.DB.prepare(
    `SELECT fr.id, u.id AS friend_user_id, u.username AS db_username, fr.created_at_utc,
            ua.content_type AS avatar_content_type,
            ua.image_base64 AS avatar_image_base64,
            ua.byte_length AS avatar_byte_length,
            ua.updated_at_utc AS avatar_updated_at_utc,
            ua.storage_provider AS avatar_storage_provider,
            ua.storage_path AS avatar_storage_path,
            ua.storage_sha AS avatar_storage_sha
       FROM friend_requests fr
       JOIN users u ON u.id = fr.sender_user_id
       LEFT JOIN user_avatars ua ON ua.user_id = u.id
      WHERE fr.recipient_user_id = ?
      ORDER BY fr.created_at_utc ASC`
  )
    .bind(auth.userId)
    .all();

  const outgoingResult = await env.DB.prepare(
    `SELECT u.id AS friend_user_id, u.username AS db_username,
            ua.content_type AS avatar_content_type,
            ua.image_base64 AS avatar_image_base64,
            ua.byte_length AS avatar_byte_length,
            ua.updated_at_utc AS avatar_updated_at_utc,
            ua.storage_provider AS avatar_storage_provider,
            ua.storage_path AS avatar_storage_path,
            ua.storage_sha AS avatar_storage_sha
       FROM friend_requests fr
       JOIN users u ON u.id = fr.recipient_user_id
       LEFT JOIN user_avatars ua ON ua.user_id = u.id
      WHERE fr.sender_user_id = ?
      ORDER BY fr.created_at_utc ASC`
  )
    .bind(auth.userId)
    .all();

  const friends = await Promise.all(
    (friendsResult.results || []).map((row) => buildFriendUserPayload(env, row, publicBaseUrl))
  );
  friends.sort((left, right) => left.username.localeCompare(right.username, "en", { sensitivity: "base" }));

  const incomingRequests = await Promise.all(
    (incomingResult.results || []).map(async (row) => ({
      id: Number(row.id),
      user: await buildFriendUserPayload(env, row, publicBaseUrl),
      createdAtUtc: String(row.created_at_utc),
    }))
  );

  const outgoingRequests = await Promise.all(
    (outgoingResult.results || []).map((row) => buildFriendUserPayload(env, row, publicBaseUrl))
  );

  return json({
    ok: true,
    friends,
    incomingRequests,
    outgoingRequests,
  });
}

async function handleSendFriendRequest(request, env) {
  const auth = await authenticate(request, env);
  if (!auth.ok) {
    return json({ ok: false, error: auth.error }, 401);
  }

  const body = await readJson(request);
  const targetUsername = normalizeUsername(body?.username);
  if (!targetUsername) {
    return json({ ok: false, error: "Invalid username" }, 400);
  }

  const targetUser = await lookupUserByUsername(env, targetUsername);
  if (!targetUser?.userId) {
    return json({ ok: false, error: "Account not found" }, 404);
  }

  const targetUserId = Number(targetUser.userId);
  if (!Number.isFinite(targetUserId) || targetUserId <= 0) {
    return json({ ok: false, error: "Account not found" }, 404);
  }

  if (targetUserId === auth.userId) {
    return json({ ok: false, error: "Cannot add yourself as a friend" }, 400);
  }

  const { lowId, highId } = normalizeFriendPair(auth.userId, targetUserId);
  const existingFriendship = await env.DB.prepare(
    `SELECT id
       FROM friendships
      WHERE user_low_id = ? AND user_high_id = ?
      LIMIT 1`
  )
    .bind(lowId, highId)
    .first();
  if (existingFriendship) {
    return json({ ok: false, error: "Already friends" }, 409);
  }

  const existingOutgoingRequest = await env.DB.prepare(
    `SELECT id
       FROM friend_requests
      WHERE sender_user_id = ? AND recipient_user_id = ?
      LIMIT 1`
  )
    .bind(auth.userId, targetUserId)
    .first();
  if (existingOutgoingRequest) {
    return json({ ok: false, error: "Friend request already sent" }, 409);
  }

  const existingIncomingRequest = await env.DB.prepare(
    `SELECT id
       FROM friend_requests
      WHERE sender_user_id = ? AND recipient_user_id = ?
      LIMIT 1`
  )
    .bind(targetUserId, auth.userId)
    .first();
  if (existingIncomingRequest) {
    return json({ ok: false, error: "Incoming friend request already exists" }, 409);
  }

  await env.DB.prepare(
    `INSERT INTO friend_requests
      (sender_user_id, recipient_user_id, created_at_utc)
      VALUES (?, ?, ?)`
  )
    .bind(auth.userId, targetUserId, new Date().toISOString())
    .run();

  return json({
    ok: true,
    targetUser: { id: targetUserId, username: String(targetUser.username) },
  });
}

async function handleRespondFriendRequest(request, env) {
  const auth = await authenticate(request, env);
  if (!auth.ok) {
    return json({ ok: false, error: auth.error }, 401);
  }

  const body = await readJson(request);
  const requestId = Number(body?.requestId);
  const action = typeof body?.action === "string" ? body.action.trim().toLowerCase() : "";
  if (!Number.isFinite(requestId) || requestId <= 0 || (action !== "accept" && action !== "decline")) {
    return json({ ok: false, error: "Invalid friend request action" }, 400);
  }

  const pendingRequest = await env.DB.prepare(
    `SELECT id, sender_user_id
       FROM friend_requests
      WHERE id = ? AND recipient_user_id = ?
      LIMIT 1`
  )
    .bind(requestId, auth.userId)
    .first();
  if (!pendingRequest) {
    return json({ ok: false, error: "Friend request not found" }, 404);
  }

  const senderUserId = Number(pendingRequest.sender_user_id);
  if (action === "accept") {
    const { lowId, highId } = normalizeFriendPair(auth.userId, senderUserId);
    await env.DB.prepare(
      `INSERT OR IGNORE INTO friendships
        (user_low_id, user_high_id, created_at_utc)
        VALUES (?, ?, ?)`
    )
      .bind(lowId, highId, new Date().toISOString())
      .run();
  }

  await env.DB.prepare("DELETE FROM friend_requests WHERE id = ?").bind(requestId).run();
  return json({ ok: true, action });
}

async function handleRemoveFriend(request, env) {
  const auth = await authenticate(request, env);
  if (!auth.ok) {
    return json({ ok: false, error: auth.error }, 401);
  }

  const body = await readJson(request);
  const targetUsername = normalizeUsername(body?.username);
  if (!targetUsername) {
    return json({ ok: false, error: "Invalid username" }, 400);
  }

  const targetUser = await lookupUserByUsername(env, targetUsername);
  if (!targetUser?.userId) {
    return json({ ok: false, error: "Account not found" }, 404);
  }

  const targetUserId = Number(targetUser.userId);
  if (!Number.isFinite(targetUserId) || targetUserId <= 0) {
    return json({ ok: false, error: "Account not found" }, 404);
  }

  if (targetUserId === auth.userId) {
    return json({ ok: false, error: "Cannot remove yourself" }, 400);
  }

  const { lowId, highId } = normalizeFriendPair(auth.userId, targetUserId);
  const friendshipDelete = await env.DB.prepare(
    `DELETE FROM friendships
      WHERE user_low_id = ? AND user_high_id = ?`
  )
    .bind(lowId, highId)
    .run();

  await env.DB.prepare(
    `DELETE FROM friend_requests
      WHERE (sender_user_id = ? AND recipient_user_id = ?)
         OR (sender_user_id = ? AND recipient_user_id = ?)`
  )
    .bind(auth.userId, targetUserId, targetUserId, auth.userId)
    .run();

  if (Number(friendshipDelete.meta?.changes || 0) <= 0) {
    return json({ ok: false, error: "Friend not found" }, 404);
  }

  return json({
    ok: true,
    removedUser: { id: targetUserId, username: String(targetUser.username) },
  });
}

async function authenticate(request, env) {
  const token = extractBearerToken(request.headers.get("Authorization"));
  if (!token) {
    return { ok: false, error: "Missing bearer token" };
  }

  const tokenHash = await sha256Hex(token);
  const row = await env.DB.prepare(
    `SELECT s.id as session_id, s.user_id, s.expires_at_utc, u.username
       FROM sessions s
       JOIN users u ON u.id = s.user_id
       WHERE s.token_hash = ?
       LIMIT 1`
  )
    .bind(tokenHash)
    .first();

  if (!row) {
    return { ok: false, error: "Invalid token" };
  }

  const expiresAt = Date.parse(String(row.expires_at_utc));
  if (!Number.isFinite(expiresAt) || expiresAt <= Date.now()) {
    await env.DB.prepare("DELETE FROM sessions WHERE id = ?").bind(Number(row.session_id)).run();
    return { ok: false, error: "Token expired" };
  }

  await env.DB.prepare("UPDATE sessions SET last_seen_at_utc = ? WHERE id = ?")
    .bind(new Date().toISOString(), Number(row.session_id))
    .run();

  const resolvedUsername = await getDisplayUsernameForUserId(
    env,
    Number(row.user_id),
    typeof row.username === "string" ? String(row.username) : null
  );

  return {
    ok: true,
    sessionId: Number(row.session_id),
    userId: Number(row.user_id),
    username: resolvedUsername || "Unknown",
    expiresAtUtc: String(row.expires_at_utc),
  };
}

async function createSession(env, userId, userAgent) {
  const rawTokenBytes = crypto.getRandomValues(new Uint8Array(32));
  const token = toBase64Url(rawTokenBytes);
  const tokenHash = await sha256Hex(token);
  const createdAtUtc = new Date().toISOString();
  const expiresAtUtc = new Date(Date.now() + DEFAULT_SESSION_DAYS * 24 * 60 * 60 * 1000).toISOString();

  await env.DB.prepare(
    `INSERT INTO sessions
      (user_id, token_hash, created_at_utc, expires_at_utc, last_seen_at_utc, user_agent)
      VALUES (?, ?, ?, ?, ?, ?)`
  )
    .bind(userId, tokenHash, createdAtUtc, expiresAtUtc, createdAtUtc, userAgent || null)
    .run();

  return { token, expiresAtUtc };
}

function buildAvatarPayload(row, username, publicBaseUrl) {
  if (!row) {
    return null;
  }

  const updatedAtUtc =
    typeof row.updated_at_utc === "string" && row.updated_at_utc.trim().length > 0
      ? String(row.updated_at_utc)
      : "";
  const storageProvider =
    typeof row.storage_provider === "string" && row.storage_provider.trim().length > 0
      ? String(row.storage_provider).trim().toLowerCase()
      : null;
  const imageBase64 =
    typeof row.image_base64 === "string" && row.image_base64.trim().length > 0
      ? String(row.image_base64)
      : null;
  const imageUrl =
    storageProvider === "github" && username
      ? buildAvatarPublicUrl(publicBaseUrl, username, updatedAtUtc)
      : null;

  if (!imageBase64 && !imageUrl) {
    return null;
  }

  return {
    contentType: String(row.content_type || "image/png"),
    imageBase64,
    imageUrl,
    byteLength: Number(row.byte_length) > 0 ? Number(row.byte_length) : null,
    updatedAtUtc,
    storageProvider,
  };
}

async function getUserAvatar(env, userId, username, publicBaseUrl) {
  const row = await env.DB.prepare(
    `SELECT content_type, image_base64, byte_length, updated_at_utc, storage_provider, storage_path, storage_sha
       FROM user_avatars
      WHERE user_id = ?
      LIMIT 1`
  )
    .bind(userId)
    .first();

  return buildAvatarPayload(row, username, publicBaseUrl);
}

async function buildFriendUserPayload(env, row, publicBaseUrl) {
  const presenceLastSeenAtUtc =
    typeof row?.last_seen_at_utc === "string" && row.last_seen_at_utc.trim().length > 0
      ? String(row.last_seen_at_utc)
      : null;
  const sessionLastSeenAtUtc =
    typeof row?.session_last_seen_at_utc === "string" && row.session_last_seen_at_utc.trim().length > 0
      ? String(row.session_last_seen_at_utc)
      : null;
  const effectiveLastSeenAtUtc = [presenceLastSeenAtUtc, sessionLastSeenAtUtc]
    .filter(Boolean)
    .sort()
    .at(-1) || null;
  const resolvedUsername = await getDisplayUsernameForUserId(
    env,
    Number(row?.friend_user_id),
    typeof row?.db_username === "string" ? String(row.db_username) : null
  );

  return {
    username: resolvedUsername || "Unknown",
    avatar: buildAvatarPayload(
      {
        content_type: row?.avatar_content_type,
        image_base64: row?.avatar_image_base64,
        byte_length: row?.avatar_byte_length,
        updated_at_utc: row?.avatar_updated_at_utc,
        storage_provider: row?.avatar_storage_provider,
        storage_path: row?.avatar_storage_path,
        storage_sha: row?.avatar_storage_sha,
      },
      resolvedUsername || "Unknown",
      publicBaseUrl
    ),
    isOnline: Number(row?.is_online || 0) > 0,
    lastSeenAtUtc: effectiveLastSeenAtUtc,
    activityKind:
      typeof row?.activity_kind === "string" && row.activity_kind.trim().length > 0
        ? String(row.activity_kind)
        : "launcher",
    activityName:
      typeof row?.activity_name === "string" && row.activity_name.trim().length > 0
        ? String(row.activity_name)
        : null,
    versionId:
      typeof row?.version_id === "string" && row.version_id.trim().length > 0
        ? String(row.version_id)
        : null,
    joinHost:
      typeof row?.join_host === "string" && row.join_host.trim().length > 0
        ? String(row.join_host)
        : null,
    joinPort: Number(row?.join_port) > 0 ? Number(row.join_port) : null,
    relayRoomId:
      typeof row?.relay_room_id === "string" && row.relay_room_id.trim().length > 0
        ? String(row.relay_room_id)
        : null,
    relayTransportMode:
      typeof row?.relay_transport_mode === "string" && row.relay_transport_mode.trim().length > 0
        ? String(row.relay_transport_mode)
        : null,
    isJoinable: Number(row?.is_joinable || 0) > 0,
  };
}

async function buildSkinProfilePayload(env, row) {
  const fallbackPayload = {
    username: typeof row?.username === "string" ? String(row.username) : "",
    uuid: typeof row?.published_uuid === "string" ? String(row.published_uuid) : "",
    offlineUuid: typeof row?.offline_uuid === "string" ? String(row.offline_uuid) : null,
    textureValue: typeof row?.texture_value === "string" ? String(row.texture_value) : "",
    textureSignature:
      typeof row?.texture_signature === "string" && row.texture_signature.trim().length > 0
        ? String(row.texture_signature)
        : null,
    textureUrl:
      typeof row?.texture_url === "string" && row.texture_url.trim().length > 0
        ? String(row.texture_url)
        : null,
    updatedAtUtc:
      typeof row?.updated_at_utc === "string" && row.updated_at_utc.trim().length > 0
        ? String(row.updated_at_utc)
        : new Date(0).toISOString(),
  };

  const storageProvider =
    typeof row?.storage_provider === "string" && row.storage_provider.trim().length > 0
      ? String(row.storage_provider).trim().toLowerCase()
      : null;
  const storagePath =
    typeof row?.storage_path === "string" && row.storage_path.trim().length > 0
      ? String(row.storage_path)
      : null;

  if (storageProvider !== "github" || !storagePath || !isGitHubAssetStorageEnabled(env)) {
    return fallbackPayload;
  }

  const githubPayload = await readGitHubJsonFile(env, storagePath);
  if (!githubPayload || typeof githubPayload !== "object") {
    return fallbackPayload;
  }

  return {
    username:
      typeof githubPayload.username === "string" && githubPayload.username.trim().length > 0
        ? String(githubPayload.username)
        : fallbackPayload.username,
    uuid:
      typeof githubPayload.uuid === "string" && githubPayload.uuid.trim().length > 0
        ? String(githubPayload.uuid)
        : fallbackPayload.uuid,
    offlineUuid:
      typeof githubPayload.offlineUuid === "string" && githubPayload.offlineUuid.trim().length > 0
        ? String(githubPayload.offlineUuid)
        : fallbackPayload.offlineUuid,
    textureValue:
      typeof githubPayload.textureValue === "string" && githubPayload.textureValue.trim().length > 0
        ? String(githubPayload.textureValue)
        : fallbackPayload.textureValue,
    textureSignature:
      typeof githubPayload.textureSignature === "string" && githubPayload.textureSignature.trim().length > 0
        ? String(githubPayload.textureSignature)
        : fallbackPayload.textureSignature,
    textureUrl:
      typeof githubPayload.textureUrl === "string" && githubPayload.textureUrl.trim().length > 0
        ? String(githubPayload.textureUrl)
        : fallbackPayload.textureUrl,
    updatedAtUtc:
      typeof githubPayload.updatedAtUtc === "string" && githubPayload.updatedAtUtc.trim().length > 0
        ? String(githubPayload.updatedAtUtc)
        : fallbackPayload.updatedAtUtc,
  };
}

function normalizeActivityKind(value) {
  const normalized =
    typeof value === "string" && value.trim().length > 0
      ? value.trim().toLowerCase()
      : "launcher";

  switch (normalized) {
    case "singleplayer":
    case "lan_host":
    case "multiplayer":
    case "in_game":
    case "launcher":
      return normalized;
    default:
      return "launcher";
  }
}

function normalizeOptionalText(value, maxLength) {
  if (typeof value !== "string") {
    return null;
  }

  const normalized = value.trim();
  if (!normalized) {
    return null;
  }

  return normalized.length > maxLength ? normalized.slice(0, maxLength) : normalized;
}

function normalizeRequiredText(value, maxLength) {
  const normalized = normalizeOptionalText(value, maxLength);
  return normalized && normalized.length > 0 ? normalized : null;
}

function normalizeUrlText(value, maxLength) {
  const normalized = normalizeOptionalText(value, maxLength);
  if (!normalized) {
    return null;
  }

  try {
    const parsed = new URL(normalized);
    if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
      return null;
    }

    return parsed.toString();
  } catch {
    return null;
  }
}

function normalizeJoinPort(value) {
  const port = Number(value);
  if (!Number.isFinite(port)) {
    return null;
  }

  const normalizedPort = Math.trunc(port);
  return normalizedPort > 0 && normalizedPort <= 65535 ? normalizedPort : null;
}

function normalizeRelayRoomId(value) {
  if (typeof value !== "string") {
    return null;
  }

  const normalized = value.trim().toLowerCase();
  return /^[a-z0-9_-]{16,80}$/.test(normalized) ? normalized : null;
}

function normalizeRelayConnectionId(value) {
  return normalizeRelayRoomId(value);
}

function normalizeRelayTransportMode(value) {
  const normalized = typeof value === "string" ? value.trim().toLowerCase() : "";
  if (normalized === RELAY_TRANSPORT_MODE) {
    return normalized;
  }

  if (normalized === `${RELAY_TRANSPORT_MODE}-overlay`) {
    return normalized;
  }

  return null;
}

function normalizeRelaySocketRole(value) {
  const normalized = typeof value === "string" ? value.trim().toLowerCase() : "";
  return normalized === "host" || normalized === "guest" ? normalized : null;
}

function createRelayRoomId() {
  return "room_" + bytesToHex(crypto.getRandomValues(new Uint8Array(12)));
}

function createRelayConnectionId() {
  return "conn_" + bytesToHex(crypto.getRandomValues(new Uint8Array(12)));
}

function buildRelayWebSocketUrl(requestUrl, connectionId, role) {
  const url = new URL(requestUrl);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  url.pathname = "/api/v1/relay/ws";
  url.search = "";
  url.searchParams.set("connectionId", connectionId);
  url.searchParams.set("role", role);
  return url.toString();
}

function extractBearerToken(authHeader) {
  if (!authHeader) {
    return null;
  }

  const parts = authHeader.trim().split(/\s+/);
  if (parts.length !== 2 || parts[0].toLowerCase() !== "bearer") {
    return null;
  }

  return parts[1];
}

function normalizeFriendPair(leftUserId, rightUserId) {
  const left = Number(leftUserId);
  const right = Number(rightUserId);
  return left <= right
    ? { lowId: left, highId: right }
    : { lowId: right, highId: left };
}

async function areFriends(env, leftUserId, rightUserId) {
  const { lowId, highId } = normalizeFriendPair(leftUserId, rightUserId);
  const row = await env.DB.prepare(
    `SELECT id
       FROM friendships
      WHERE user_low_id = ? AND user_high_id = ?
      LIMIT 1`
  )
    .bind(lowId, highId)
    .first();
  return !!row;
}

async function resolveActiveRelaySessionForUserId(env, userId) {
  const row = await env.DB.prepare(
    `SELECT room_id, host_user_id, transport_mode, updated_at_utc
       FROM relay_sessions
      WHERE host_user_id = ?
        AND is_active = 1
      LIMIT 1`
  )
    .bind(userId)
    .first();

  if (!row) {
    return null;
  }

  const updatedAt = Date.parse(String(row.updated_at_utc || ""));
  if (!Number.isFinite(updatedAt) || updatedAt + RELAY_SESSION_TTL_MS <= Date.now()) {
    return null;
  }

  return {
    roomId: String(row.room_id),
    hostUserId: Number(row.host_user_id),
    transportMode: String(row.transport_mode || RELAY_TRANSPORT_MODE),
  };
}

async function resolveActiveRelaySessionByRoomId(env, roomId) {
  const row = await env.DB.prepare(
    `SELECT room_id, host_user_id, transport_mode, updated_at_utc
       FROM relay_sessions
      WHERE room_id = ?
        AND is_active = 1
      LIMIT 1`
  )
    .bind(roomId)
    .first();

  if (!row) {
    return null;
  }

  const updatedAt = Date.parse(String(row.updated_at_utc || ""));
  if (!Number.isFinite(updatedAt) || updatedAt + RELAY_SESSION_TTL_MS <= Date.now()) {
    return null;
  }

  return {
    roomId: String(row.room_id),
    hostUserId: Number(row.host_user_id),
    transportMode: String(row.transport_mode || RELAY_TRANSPORT_MODE),
  };
}

async function cleanupExpiredRelayConnections(env, hostUserId) {
  const expiryIso = new Date(Date.now() - RELAY_CONNECTION_TTL_MS).toISOString();
  await env.DB.prepare(
    `UPDATE relay_connections
        SET status = 'closed',
            updated_at_utc = ?,
            closed_at_utc = COALESCE(closed_at_utc, ?)
      WHERE host_user_id = ?
        AND status != 'closed'
        AND created_at_utc < ?`
  )
    .bind(new Date().toISOString(), new Date().toISOString(), hostUserId, expiryIso)
    .run();
}

async function readJson(request) {
  const text = await request.text();
  if (!text || !text.trim()) {
    return {};
  }

  try {
    return JSON.parse(text);
  } catch {
    return {};
  }
}

function normalizeUsername(value) {
  if (typeof value !== "string") {
    return null;
  }

  const username = value.trim();
  if (!USERNAME_RE.test(username)) {
    return null;
  }

  return username;
}

function normalizeUuid(value) {
  if (typeof value !== "string") {
    return null;
  }

  const compact = value.trim().replace(/-/g, "").toLowerCase();
  if (!/^[0-9a-f]{32}$/.test(compact)) {
    return null;
  }

  return `${compact.slice(0, 8)}-${compact.slice(8, 12)}-${compact.slice(12, 16)}-${compact.slice(16, 20)}-${compact.slice(20)}`;
}

function isHex(value) {
  return typeof value === "string" && value.length > 0 && value.length % 2 === 0 && /^[0-9a-fA-F]+$/.test(value);
}

function hexToBytes(hex) {
  const normalized = String(hex).trim();
  const bytes = new Uint8Array(normalized.length / 2);
  for (let i = 0; i < normalized.length; i += 2) {
    bytes[i / 2] = parseInt(normalized.slice(i, i + 2), 16);
  }
  return bytes;
}

function bytesToHex(bytes) {
  return [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
}

async function sha256Hex(text) {
  const digest = await crypto.subtle.digest("SHA-256", encoder.encode(text));
  return bytesToHex(new Uint8Array(digest));
}

async function derivePbkdf2Sha256(password, saltBytes, iterations, outputLengthBytes) {
  try {
    const keyMaterial = await crypto.subtle.importKey(
      "raw",
      encoder.encode(password),
      "PBKDF2",
      false,
      ["deriveBits"]
    );
    const bits = await crypto.subtle.deriveBits(
      {
        name: "PBKDF2",
        hash: "SHA-256",
        salt: saltBytes,
        iterations,
      },
      keyMaterial,
      outputLengthBytes * 8
    );
    return new Uint8Array(bits);
  } catch (error) {
    throw new Error(`Pbkdf2 failed: ${String(error?.message || error)}`);
  }
}

async function computePasswordHashForStoredCredentials(password, storedSaltHex, storedIterations, outputLengthBytes) {
  if (!isHex(storedSaltHex)) {
    throw new Error("Stored credential format is invalid");
  }

  const salt = hexToBytes(storedSaltHex);
  const iterations = Number(storedIterations) > 0 ? Number(storedIterations) : DEFAULT_PASSWORD_ITERATIONS;
  if (iterations > 100000) {
    throw new Error("Password verification must be done client-side for this account");
  }

  return derivePbkdf2Sha256(password, salt, iterations, outputLengthBytes);
}

function timingSafeEqual(a, b) {
  if (!(a instanceof Uint8Array) || !(b instanceof Uint8Array) || a.length !== b.length) {
    return false;
  }

  let diff = 0;
  for (let i = 0; i < a.length; i++) {
    diff |= a[i] ^ b[i];
  }
  return diff === 0;
}

function toBase64Url(bytes) {
  let bin = "";
  for (let i = 0; i < bytes.length; i++) {
    bin += String.fromCharCode(bytes[i]);
  }
  return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function json(payload, status = 200, origin = "*") {
  const response = new Response(JSON.stringify(payload, null, 2), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
    },
  });
  return withCors(response, origin);
}

function withCors(response, origin = "*") {
  response.headers.set("Access-Control-Allow-Origin", origin);
  response.headers.set("Access-Control-Allow-Methods", "GET,POST,OPTIONS");
  response.headers.set("Access-Control-Allow-Headers", "Content-Type, Authorization");
  response.headers.set("Vary", "Origin");
  return response;
}

export class RelayConnectionDurableObject {
  constructor(state, env) {
    this.state = state;
    this.env = env;
    this.sockets = new Map();
    this.pendingByRole = {
      host: [],
      guest: [],
    };

    for (const socket of this.state.getWebSockets()) {
      const attachment = socket.deserializeAttachment() || {};
      const role = attachment.role === "guest" ? "guest" : "host";
      this.sockets.set(socket, { role });
    }
  }

  async fetch(request) {
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
      return new Response("Expected websocket upgrade", { status: 426 });
    }

    const role = normalizeRelaySocketRole(request.headers.get("X-Vesper-Relay-Role"));
    if (!role) {
      return new Response("Invalid relay role", { status: 400 });
    }

    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];
    this.state.acceptWebSocket(server);
    server.serializeAttachment({ role });
    this.sockets.set(server, { role });
    this.flushPending();

    return new Response(null, {
      status: 101,
      webSocket: client,
    });
  }

  webSocketMessage(socket, message) {
    const entry = this.sockets.get(socket);
    if (!entry) {
      socket.close(1011, "Relay socket state missing");
      return;
    }

    const peerRole = entry.role === "host" ? "guest" : "host";
    const peer = this.findPeer(peerRole);
    if (peer) {
      peer.send(message);
      return;
    }

    this.queuePending(peerRole, message);
  }

  webSocketClose(socket) {
    this.detachSocket(socket, 1000, "peer closed");
  }

  webSocketError(socket) {
    this.detachSocket(socket, 1011, "peer error");
  }

  findPeer(role) {
    for (const [socket, entry] of this.sockets.entries()) {
      if (entry.role === role && socket.readyState === 1) {
        return socket;
      }
    }

    return null;
  }

  queuePending(role, message) {
    const queue = role === "guest" ? this.pendingByRole.guest : this.pendingByRole.host;
    if (queue.length >= 64) {
      queue.shift();
    }
    queue.push(message);
  }

  flushPending() {
    for (const role of ["host", "guest"]) {
      const socket = this.findPeer(role);
      if (!socket) {
        continue;
      }

      const queue = role === "guest" ? this.pendingByRole.guest : this.pendingByRole.host;
      while (queue.length > 0) {
        socket.send(queue.shift());
      }
    }
  }

  detachSocket(socket, closeCode, closeReason) {
    const entry = this.sockets.get(socket);
    this.sockets.delete(socket);

    const peer = entry ? this.findPeer(entry.role === "host" ? "guest" : "host") : null;
    if (peer && peer.readyState === 1) {
      peer.close(closeCode, closeReason);
    }
  }
}
