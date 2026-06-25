import fs from "node:fs";
import path from "node:path";

const GITHUB_API_VERSION = "2022-11-28";

function readDotEnv(filePath) {
  if (!fs.existsSync(filePath)) {
    return {};
  }

  const values = {};
  const text = fs.readFileSync(filePath, "utf8").replace(/^\uFEFF/, "");
  for (const rawLine of text.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith("#") || !line.includes("=")) {
      continue;
    }

    const index = line.indexOf("=");
    const name = line.slice(0, index).trim();
    let value = line.slice(index + 1).trim();
    if (
      (value.startsWith('"') && value.endsWith('"')) ||
      (value.startsWith("'") && value.endsWith("'"))
    ) {
      value = value.slice(1, -1);
    }

    values[name] = value;
  }

  return values;
}

function readConfigValue(values, names) {
  for (const name of names) {
    const value = values[name] ?? process.env[name];
    if (typeof value === "string" && value.trim().length > 0) {
      return value.trim();
    }
  }

  return null;
}

function splitRepository(value) {
  if (typeof value !== "string" || !value.includes("/")) {
    return null;
  }

  const parts = value
    .split("/")
    .map((part) => part.trim())
    .filter(Boolean);

  return parts.length === 2 ? { owner: parts[0], repo: parts[1] } : null;
}

function resolveConfig(values) {
  const repository = splitRepository(readConfigValue(values, ["GITHUB_STORAGE_REPOSITORY", "GITHUB_REPOSITORY"]));
  const repoValue = readConfigValue(values, ["GITHUB_STORAGE_REPO", "GITHUB_REPO_NAME"]);
  const repoFromValue = splitRepository(repoValue);

  return {
    owner:
      readConfigValue(values, ["GITHUB_STORAGE_OWNER", "GITHUB_REPO_OWNER"]) ||
      repoFromValue?.owner ||
      repository?.owner ||
      null,
    repo: repoFromValue?.repo || repoValue || repository?.repo || null,
    branch: readConfigValue(values, ["GITHUB_STORAGE_BRANCH", "GITHUB_REPO_BRANCH"]) || "main",
    token: readConfigValue(values, ["GITHUB_STORAGE_TOKEN", "GITHUB_TOKEN"]),
  };
}

async function fetchGitHub(config, url) {
  const response = await fetch(url, {
    headers: {
      Accept: "application/vnd.github+json",
      Authorization: `Bearer ${config.token}`,
      "User-Agent": "vesper-account-api-check",
      "X-GitHub-Api-Version": GITHUB_API_VERSION,
    },
  });

  let body = null;
  try {
    body = await response.json();
  } catch {
    body = null;
  }

  return { response, body };
}

function describeStatus(status) {
  if (status === 200) {
    return "OK";
  }

  if (status === 401) {
    return "token rejected by GitHub";
  }

  if (status === 403) {
    return "token is forbidden or rate-limited";
  }

  if (status === 404) {
    return "repo/branch not found or token cannot access private repo";
  }

  return "unexpected response";
}

async function main() {
  const envPath = path.resolve(".env");
  const values = readDotEnv(envPath);
  const config = resolveConfig(values);
  const missing = Object.entries({
    GITHUB_STORAGE_OWNER: config.owner,
    GITHUB_STORAGE_REPO: config.repo,
    GITHUB_STORAGE_TOKEN: config.token,
  })
    .filter(([, value]) => !value)
    .map(([name]) => name);

  if (missing.length > 0) {
    console.error(`GitHub asset storage config is incomplete. Missing: ${missing.join(", ")}`);
    process.exitCode = 1;
    return;
  }

  const repoSlug = `${config.owner}/${config.repo}`;
  console.log(`Checking GitHub asset storage for ${repoSlug} on branch ${config.branch}.`);
  console.log("Token value is intentionally not printed.");

  const repoUrl = `https://api.github.com/repos/${encodeURIComponent(config.owner)}/${encodeURIComponent(config.repo)}`;
  const repoCheck = await fetchGitHub(config, repoUrl);
  console.log(`Repo access: HTTP ${repoCheck.response.status} (${describeStatus(repoCheck.response.status)})`);

  if (!repoCheck.response.ok) {
    process.exitCode = 1;
    return;
  }

  const permissions = repoCheck.body?.permissions;
  if (permissions && typeof permissions === "object") {
    console.log(`Repo permissions: pull=${Boolean(permissions.pull)}, push=${Boolean(permissions.push)}`);
    if (!permissions.push) {
      console.log("Warning: token can read the repo, but write access was not detected.");
    }
  }

  const branchUrl = `${repoUrl}/branches/${encodeURIComponent(config.branch)}`;
  const branchCheck = await fetchGitHub(config, branchUrl);
  console.log(`Branch access: HTTP ${branchCheck.response.status} (${describeStatus(branchCheck.response.status)})`);

  if (!branchCheck.response.ok) {
    process.exitCode = 1;
    return;
  }

  console.log("GitHub asset storage check passed.");
}

main().catch((error) => {
  console.error("GitHub asset storage check failed:", error?.message || error);
  process.exitCode = 1;
});
