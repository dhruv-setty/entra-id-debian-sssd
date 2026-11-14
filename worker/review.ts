// Agent Q single-step reviewer for Cloudflare Workers.
// One request: (1) fetch the change from GitHub, (2) read the repository's data points from Supabase,
// (3) apply the deterministic Agent Q rules, (4) make one Workers AI call, (5) save the review to Supabase.
import { createClient, type SupabaseClient } from "@supabase/supabase-js";
import { addedLines, type Finding, reviewLines, reviewText, summarise } from "../supabase/functions/agent-q/rules.ts";

export interface Env {
  AI: { run: (model: string, input: unknown) => Promise<any> };
  SUPABASE_URL: string;
  SUPABASE_SERVICE_ROLE_KEY?: string;
  GITHUB_TOKEN?: string;
  AGENT_Q_MODEL?: string;
}

export interface ReviewRequest {
  owner?: string;
  repo: string;
  number?: number; // pull request
  ref?: string; // commit
  path?: string; // text review
  content?: string;
}

const DEFAULT_MODEL = "@cf/meta/llama-3.1-8b-instruct-fast";
const MAX_DIFF_CHARS = 12_000;

let client: SupabaseClient | undefined;
const db = (env: Env) => {
  if (!env.SUPABASE_SERVICE_ROLE_KEY) return undefined;
  client ??= createClient(env.SUPABASE_URL, env.SUPABASE_SERVICE_ROLE_KEY, { auth: { persistSession: false, autoRefreshToken: false } });
  return client;
};

async function gh(path: string, token?: string) {
  const res = await fetch(`https://api.github.com${path}`, {
    headers: {
      accept: "application/vnd.github+json",
      "user-agent": "agent-q-worker",
      "x-github-api-version": "2022-11-28",
      ...(token ? { authorization: `Bearer ${token}` } : {}),
    },
  });
  if (!res.ok) throw new Error(`GitHub ${res.status} on ${path}`);
  return res.json();
}

// Step 1: the change under review, as files with their added lines and a unified diff excerpt.
async function fetchChange(req: ReviewRequest, owner: string, token?: string) {
  if (req.content !== undefined) {
    return { kind: "text" as const, target: req.path ?? "snippet.txt", files: [{ name: req.path ?? "snippet.txt", content: req.content }], diff: req.content };
  }
  if (req.number) {
    const files: any[] = [];
    for (let p = 1; p <= 10; p++) {
      const page = await gh(`/repos/${owner}/${req.repo}/pulls/${req.number}/files?per_page=100&page=${p}`, token);
      files.push(...page);
      if (page.length < 100) break;
    }
    const live = files.filter((f) => f.status !== "removed" && f.patch);
    return { kind: "pull_request" as const, target: `#${req.number}`, files: live.map((f) => ({ name: f.filename, patch: f.patch as string })), diff: live.map((f) => `--- ${f.filename}\n${f.patch}`).join("\n") };
  }
  if (req.ref) {
    const c = await gh(`/repos/${owner}/${req.repo}/commits/${encodeURIComponent(req.ref)}`, token);
    const live = (c.files ?? []).filter((f: any) => f.patch);
    return { kind: "commit" as const, target: String(c.sha).slice(0, 7), files: live.map((f: any) => ({ name: f.filename, patch: f.patch as string })), diff: live.map((f: any) => `--- ${f.filename}\n${f.patch}`).join("\n") };
  }
  throw new Error("Give number (pull request), ref (commit) or content (text).");
}

// Step 4: one LLM call. It explains and prioritises; it never changes the deterministic verdict.
async function aiSummary(env: Env, repo: string, dataPoints: Record<string, unknown>, findings: Finding[], diff: string) {
  const model = env.AGENT_Q_MODEL || DEFAULT_MODEL;
  const messages = [
    { role: "system", content: "You are Agent Q, a strict code reviewer. Using the repository context, the rule findings and the diff, write a short review in plain English: at most 5 bullet points covering the most important risks and one concrete fix each. Do not restate the verdict. Do not invent findings that the diff does not support." },
    { role: "user", content: `Repository: ${repo}\nRepository data points: ${JSON.stringify(dataPoints).slice(0, 2000)}\nRule findings: ${JSON.stringify(findings.slice(0, 40))}\nDiff (truncated):\n${diff.slice(0, MAX_DIFF_CHARS)}` },
  ];
  try {
    const out = await env.AI.run(model, { messages, max_tokens: 500 });
    return { model, text: String(out?.response ?? "").trim() || null };
  } catch (e) {
    return { model, text: null, error: (e as Error).message };
  }
}

export async function review(env: Env, req: ReviewRequest, callerToken?: string) {
  const owner = req.owner || "whatheheckisthis";
  if (!req.repo) throw new Error("repo is required");
  const full = `${owner}/${req.repo}`;
  const token = callerToken || env.GITHUB_TOKEN;
  const supabase = db(env);

  // Steps 1 and 2 run together.
  const [change, points] = await Promise.all([
    fetchChange(req, owner, token),
    supabase ? supabase.from("repo_data_points").select("key,value").eq("repo", full) : Promise.resolve({ data: [], error: null }),
  ]);
  const dataPoints: Record<string, unknown> = {};
  for (const p of (points as any).data ?? []) dataPoints[p.key] = p.value;

  // Step 3: deterministic rules.
  const findings: Finding[] = [];
  for (const f of change.files as any[]) {
    if (f.content !== undefined) findings.push(...reviewText(f.name, f.content));
    else findings.push(...reviewLines(f.name, addedLines(f.patch)));
  }
  const { verdict, counts } = summarise(findings);

  // Step 4: single LLM call.
  const ai = await aiSummary(env, full, dataPoints, findings, change.diff);

  // Step 5: persist the review and refresh the repository's data points.
  let id: string | null = null;
  let saved = false;
  if (supabase) {
    const row = { repo: full, kind: change.kind, target: change.target, verdict, errors: counts.error, warnings: counts.warning, notes: counts.info, findings, ai_summary: ai.text, model: ai.model, data_points: dataPoints };
    const ins = await supabase.from("reviews").insert(row).select("id").single();
    id = ins.data?.id ?? null;
    saved = !ins.error;
    await supabase.from("repo_data_points").upsert([
      { repo: full, key: "last_review", value: { kind: change.kind, target: change.target, verdict, counts, at: new Date().toISOString() }, updated_at: new Date().toISOString() },
    ]);
  }

  return { repo: full, kind: change.kind, target: change.target, verdict, counts, findings, ai_review: ai.text, model: ai.model, ai_error: (ai as any).error, data_points: dataPoints, review_id: id, saved };
}

export async function history(env: Env, owner: string, repo: string, limit = 20) {
  const supabase = db(env);
  if (!supabase) return [];
  const { data, error } = await supabase.from("reviews").select("id,created_at,kind,target,verdict,errors,warnings,notes,model").eq("repo", `${owner}/${repo}`).order("created_at", { ascending: false }).limit(limit);
  if (error) throw new Error(error.message);
  return data;
}
