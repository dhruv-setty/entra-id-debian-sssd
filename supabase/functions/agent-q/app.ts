import { Hono } from "npm:hono@4";
import { cors } from "npm:hono@4/cors";
import { McpServer } from "npm:@modelcontextprotocol/sdk@1.32.2/server/mcp.js";
import { WebStandardStreamableHTTPServerTransport } from "npm:@modelcontextprotocol/sdk@1.32.2/server/webStandardStreamableHttp.js";
import { z } from "npm:zod@^4.1";
import { AsyncLocalStorage } from "node:async_hooks";
import { addedLines, Finding, POLICY, reviewLines, reviewText, summarise, toMarkdown } from "./rules.ts";

export const VERSION = "0.1.1";
const OWNER_DEFAULT = "whatheheckisthis";

const env = (k: string): string | undefined =>
  (globalThis as any).Deno?.env?.get(k) ?? (globalThis as any).process?.env?.[k];

// A caller MAY supply its own GitHub token per request (x-github-token); it is used for that request only and never stored.
const requestToken = new AsyncLocalStorage<string | undefined>();

async function gh(path: string) {
  const token = requestToken.getStore() ?? env("GITHUB_TOKEN");
  const res = await fetch(`https://api.github.com${path}`, {
    headers: {
      accept: "application/vnd.github+json",
      "user-agent": "agent-q",
      "x-github-api-version": "2022-11-28",
      ...(token ? { authorization: `Bearer ${token}` } : {}),
    },
  });
  if (!res.ok) throw new Error(`GitHub ${res.status} on ${path}`);
  return res.json();
}

async function ghAll(path: string, max = 10) {
  const out: any[] = [];
  for (let p = 1; p <= max; p++) {
    const page = await gh(`${path}${path.includes("?") ? "&" : "?"}per_page=100&page=${p}`);
    out.push(...page);
    if (page.length < 100) break;
  }
  return out;
}

const text = (t: string) => ({ content: [{ type: "text" as const, text: t }] });
const result = (title: string, findings: Finding[]) =>
  text(toMarkdown(title, findings) + "\n<details><summary>JSON</summary>\n\n```json\n" + JSON.stringify({ ...summarise(findings), findings }, null, 2) + "\n```\n</details>\n");

export function buildServer() {
  const server = new McpServer({ name: "agent-q", version: VERSION });

  server.registerResource("policy", "agentq://policy", { title: "Agent Q review policy", mimeType: "text/markdown" },
    async (uri: URL) => ({ contents: [{ uri: uri.href, text: POLICY }] }));

  server.registerTool("get_review_policy", {
    title: "Get review policy",
    description: "Return the Agent Q review policy: rule identifiers, severities and requirements.",
    inputSchema: {},
    annotations: { readOnlyHint: true },
  }, async () => text(POLICY));

  server.registerTool("review_text", {
    title: "Review text",
    description: "Review a file's content against the Agent Q policy (secrets, normative documentation style, evidence, hygiene).",
    inputSchema: { path: z.string().describe("File path, used to select documentation rules"), content: z.string().max(500_000) },
    annotations: { readOnlyHint: true },
  }, async ({ path, content }: { path: string; content: string }) => result(path, reviewText(path, content)));

  server.registerTool("review_pull_request", {
    title: "Review pull request",
    description: "Fetch a pull request's changed files from GitHub and review only the added lines against the Agent Q policy.",
    inputSchema: {
      owner: z.string().default(OWNER_DEFAULT),
      repo: z.string(),
      number: z.number().int().positive(),
    },
    annotations: { readOnlyHint: true, openWorldHint: true },
  }, async ({ owner, repo, number }: { owner: string; repo: string; number: number }) => {
    const files = await ghAll(`/repos/${owner}/${repo}/pulls/${number}/files`, 30);
    const findings: Finding[] = [];
    for (const f of files) {
      if (f.status === "removed" || !f.patch) continue;
      findings.push(...reviewLines(f.filename, addedLines(f.patch)));
    }
    return result(`${owner}/${repo}#${number}`, findings);
  });

  server.registerTool("review_commit", {
    title: "Review commit",
    description: "Review the added lines of a single commit against the Agent Q policy, and report whether the commit signature is verified.",
    inputSchema: { owner: z.string().default(OWNER_DEFAULT), repo: z.string(), ref: z.string() },
    annotations: { readOnlyHint: true, openWorldHint: true },
  }, async ({ owner, repo, ref }: { owner: string; repo: string; ref: string }) => {
    const c = await gh(`/repos/${owner}/${repo}/commits/${encodeURIComponent(ref)}`);
    const findings: Finding[] = [];
    for (const f of c.files ?? []) if (f.patch) findings.push(...reviewLines(f.filename, addedLines(f.patch)));
    const v = c.commit?.verification;
    if (v && !v.verified) findings.push({ rule: "AQ-REP-02", severity: "warning", file: "(commit)", line: 0, message: `Commit signature is not verified (${v.reason}).` });
    return result(`${owner}/${repo}@${String(c.sha).slice(0, 7)}`, findings);
  });

  server.registerTool("review_repository", {
    title: "Review repository",
    description: "Audit a repository's baseline: required files, CI workflow, signing key, and the README against the documentation rules.",
    inputSchema: { owner: z.string().default(OWNER_DEFAULT), repo: z.string() },
    annotations: { readOnlyHint: true, openWorldHint: true },
  }, async ({ owner, repo }: { owner: string; repo: string }) => {
    const r = await gh(`/repos/${owner}/${repo}`);
    const tree = await gh(`/repos/${owner}/${repo}/git/trees/${r.default_branch}?recursive=1`);
    const paths: string[] = (tree.tree ?? []).map((t: any) => t.path);
    const has = (re: RegExp) => paths.some((p) => re.test(p));
    const findings: Finding[] = [];
    const need: [RegExp, string][] = [[/^README\.md$/i, "README.md"], [/^LICEN[CS]E/i, "LICENSE"], [/^(\.github\/)?SECURITY\.md$/i, "SECURITY.md"], [/^\.github\/workflows\/.+\.ya?ml$/, "a CI workflow"]];
    for (const [re, name] of need) if (!has(re)) findings.push({ rule: "AQ-REP-01", severity: "warning", file: "(repository)", line: 0, message: `Missing ${name}.` });
    if (!has(/^KEYS\.asc$/)) findings.push({ rule: "AQ-REP-02", severity: "info", file: "(repository)", line: 0, message: "No KEYS.asc signing key published." });
    if (!r.description) findings.push({ rule: "AQ-REP-01", severity: "info", file: "(repository)", line: 0, message: "Repository has no description." });
    const readme = paths.find((p) => /^README\.md$/i.test(p));
    if (readme) {
      const blob = await gh(`/repos/${owner}/${repo}/contents/${readme}?ref=${r.default_branch}`);
      const body = new TextDecoder().decode(Uint8Array.from(atob(String(blob.content).replace(/\n/g, "")), (ch) => ch.charCodeAt(0)));
      findings.push(...reviewText(readme, body));
    }
    return result(`${owner}/${repo}`, findings);
  });

  server.registerTool("list_repositories", {
    title: "List repositories",
    description: "List the public repositories of a GitHub account that Agent Q can review.",
    inputSchema: { owner: z.string().default(OWNER_DEFAULT) },
    annotations: { readOnlyHint: true, openWorldHint: true },
  }, async ({ owner }: { owner: string }) => {
    const repos = await ghAll(`/users/${owner}/repos?type=owner&sort=updated`, 3);
    return text(repos.map((r: any) => `- ${r.full_name}${r.private ? " (private)" : ""}: ${r.description ?? "no description"}`).join("\n"));
  });

  return server;
}

export function buildApp(basePath = "/agent-q") {
  const app = new Hono().basePath(basePath);
  app.use("*", cors({ origin: "*", allowHeaders: ["authorization", "x-github-token", "content-type", "accept", "mcp-session-id", "mcp-protocol-version"], exposeHeaders: ["mcp-session-id"] }));

  app.use("/mcp", async (c, next) => {
    const required = env("AGENT_Q_TOKEN");
    if (required && c.req.header("authorization") !== `Bearer ${required}`) return c.json({ error: "unauthorized" }, 401);
    await next();
  });

  app.get("/", (c) => c.json({ name: "agent-q", version: VERSION, mcp: `${basePath}/mcp`, transport: "streamable-http" }));
  app.get("/health", (c) => c.json({ ok: true, version: VERSION }));

  app.all("/mcp", async (c) => {
    // Stateless: a fresh server and transport per request, as Edge Functions keep no session state.
    const server = buildServer();
    const transport = new WebStandardStreamableHTTPServerTransport({ sessionIdGenerator: undefined, enableJsonResponse: true });
    await server.connect(transport);
    const callerToken = c.req.header("x-github-token") || undefined;
    return requestToken.run(callerToken, () => transport.handleRequest(c.req.raw));
  });

  return app;
}
