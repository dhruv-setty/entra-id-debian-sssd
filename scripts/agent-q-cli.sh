#!/usr/bin/env bash
# Agent Q: scaffold, run locally, optionally deploy, and register with Claude Code. CLI only.
# Usage:   ./agent-q-cli.sh [dir]
#          DEPLOY=1 ./agent-q-cli.sh [dir]        # also deploy to production
# Needs:   supabase CLI, Docker (running), curl, jq, claude (Claude Code CLI)
set -euo pipefail

DIR="${1:-agent-q-local}"
FN="agent-q"
REF="${SUPABASE_PROJECT_REF:-tcaefdirgffncwfwrlbe}"
SRC="https://raw.githubusercontent.com/whatheheckisthis/agent-q/main/supabase/functions/agent-q"
LOCAL_URL="http://127.0.0.1:54321/functions/v1/${FN}/mcp"
PROD_URL="https://${REF}.supabase.co/functions/v1/${FN}/mcp"
HDR=(-H "Content-Type: application/json" -H "Accept: application/json, text/event-stream")

say() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
need() { command -v "$1" >/dev/null || { echo "Missing: $1"; exit 1; }; }
for c in supabase docker curl jq claude; do need "$c"; done
docker info >/dev/null 2>&1 || { echo "Docker is not running. Start Docker Desktop and retry."; exit 1; }

say "1. Scaffold"
mkdir -p "$DIR" && cd "$DIR"
[ -f supabase/config.toml ] || supabase init --force
[ -d "supabase/functions/$FN" ] || supabase functions new "$FN"

say "2. Write the MCP server (official MCP TypeScript SDK + Hono)"
for f in index.ts app.ts rules.ts deno.json; do
  curl -fsSL "$SRC/$f" -o "supabase/functions/$FN/$f"
done
# Public MCP server: no Supabase JWT check on this function.
if grep -q "^\[functions\.$FN\]" supabase/config.toml; then
  sed -i.bak "/^\[functions\.$FN\]/,/^\[/ s/^verify_jwt = .*/verify_jwt = false/" supabase/config.toml && rm -f supabase/config.toml.bak
else
  printf '\n[functions.%s]\nverify_jwt = false\n' "$FN" >> supabase/config.toml
fi

say "3. Start the local stack and serve"
supabase start
supabase functions serve "$FN" --no-verify-jwt > serve.log 2>&1 &
SERVE_PID=$!
echo "serve PID $SERVE_PID (logs: $DIR/serve.log)"
for i in $(seq 1 60); do
  curl -fs "http://127.0.0.1:54321/functions/v1/$FN/health" >/dev/null 2>&1 && break
  sleep 1
done

say "4. Smoke test over raw JSON-RPC"
curl -fsS -X POST "$LOCAL_URL" "${HDR[@]}" -d '{
  "jsonrpc":"2.0","id":1,"method":"initialize",
  "params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"cli-test","version":"1.0.0"}}}' | jq '.result.serverInfo'
curl -fsS -X POST "$LOCAL_URL" "${HDR[@]}" -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | jq -r '.result.tools[].name'
curl -fsS -X POST "$LOCAL_URL" "${HDR[@]}" -d '{"jsonrpc":"2.0","id":3,"method":"tools/call",
  "params":{"name":"review_text","arguments":{"path":"README.md","content":"The system must be fully secure."}}}' \
  | jq -r '.result.content[0].text' | head -8

say "5. Register the local server with Claude Code"
claude mcp remove agent-q-local >/dev/null 2>&1 || true
claude mcp add --transport http agent-q-local "$LOCAL_URL"

if [ "${DEPLOY:-0}" = "1" ]; then
  say "6. Deploy to production ($REF)"
  # Uses SUPABASE_ACCESS_TOKEN if set, otherwise opens a browser login once.
  [ -n "${SUPABASE_ACCESS_TOKEN:-}" ] || supabase login
  supabase link --project-ref "$REF"
  supabase functions deploy "$FN" --no-verify-jwt
  curl -fsS -X POST "$PROD_URL" "${HDR[@]}" -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | jq -r '.result.tools[].name'
  claude mcp remove agent-q >/dev/null 2>&1 || true
  if [ -n "${AGENT_Q_TOKEN:-}" ]; then
    claude mcp add --transport http agent-q "$PROD_URL" --header "Authorization: Bearer $AGENT_Q_TOKEN"
  else
    claude mcp add --transport http agent-q "$PROD_URL"
  fi
fi

say "Done"
claude mcp list
echo
echo "Run 'claude' in this folder, then type /mcp to see Agent Q's status."
echo "Stop the local server:  kill $SERVE_PID && supabase stop"
