// Agent Q on Cloudflare Workers. Same app as the Supabase Edge Function, served at the Worker root.
// Secrets (AGENT_Q_TOKEN, GITHUB_TOKEN) are Worker variables; with nodejs_compat they appear on process.env.
import { buildApp } from "../supabase/functions/agent-q/app.ts";

const app = buildApp("/");

export default { fetch: app.fetch };
