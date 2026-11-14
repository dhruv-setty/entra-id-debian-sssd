// Agent Q: MCP review server on Supabase Edge Functions.
import "jsr:@supabase/functions-js/edge-runtime.d.ts";
import { buildApp } from "./app.ts";

const app = buildApp("/agent-q");
Deno.serve(app.fetch);
