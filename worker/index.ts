// Agent Q on Cloudflare Workers. The MCP server (/mcp) plus the single-step AI reviewer (/review).
// Vars: SUPABASE_URL, AGENT_Q_API_KEY (publishable gateway key). Secret: SUPABASE_SERVICE_ROLE_KEY. Binding: AI.
// With nodejs_compat, vars and secrets also appear on process.env, which the shared MCP app reads.
import { buildApp } from "../supabase/functions/agent-q/app.ts";
import { type Env, history, review } from "./review.ts";

const app = buildApp("/");

const allowed = (c: any) => {
  const key = (c.env as any)?.AGENT_Q_API_KEY;
  if (!key) return true;
  const bearer = (c.req.header("authorization") ?? "").replace(/^Bearer\s+/i, "");
  return c.req.header("apikey") === key || bearer === key;
};

app.post("/review", async (c) => {
  if (!allowed(c)) return c.json({ error: "unauthorized", hint: "send the apikey header" }, 401);
  try {
    const body = await c.req.json();
    return c.json(await review(c.env as unknown as Env, body, c.req.header("x-github-token") || undefined));
  } catch (e) {
    return c.json({ error: (e as Error).message }, 400);
  }
});

app.get("/reviews/:owner/:repo", async (c) => {
  if (!allowed(c)) return c.json({ error: "unauthorized" }, 401);
  try {
    return c.json(await history(c.env as unknown as Env, c.req.param("owner"), c.req.param("repo"), Number(c.req.query("limit") ?? 20)));
  } catch (e) {
    return c.json({ error: (e as Error).message }, 500);
  }
});

export default { fetch: app.fetch };
