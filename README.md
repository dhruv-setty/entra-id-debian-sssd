# Agent Q

Agent Q is a Model Context Protocol (MCP) review server for repository governance. It applies a deterministic policy to pull requests, commits and repositories, and returns findings by rule identifier.

## 1. Scope

| Item | Value |
|---|---|
| Runtime | Supabase Edge Functions (Deno) |
| Protocol | MCP, Streamable HTTP transport, stateless |
| Libraries | `@modelcontextprotocol/sdk` 1.32.2, Hono 4 |
| Clients | GitHub Copilot custom agent, GitHub Actions, any MCP client |
| Endpoint | `https://tcaefdirgffncwfwrlbe.supabase.co/functions/v1/agent-q/mcp` |

## 2. Conventions

The key words MUST, MUST NOT, SHALL, SHALL NOT, SHOULD and MAY are to be read as described in RFC 2119 and RFC 8174.

## 3. Requirements

| ID | Requirement |
|---|---|
| AQ-SRV-01 | The server SHALL expose its tools over the MCP Streamable HTTP transport at `/agent-q/mcp`. |
| AQ-SRV-02 | The server SHALL hold no session state between requests. |
| AQ-SRV-03 | Every tool SHALL be read only with respect to the reviewed repository. |
| AQ-SRV-04 | The server SHALL reject requests without the bearer token when `AGENT_Q_TOKEN` is set. |
| AQ-SRV-05 | The server MUST NOT return a detected credential in any response. |
| AQ-SRV-06 | Findings SHALL carry a rule identifier, a severity and a file and line location. |
| AQ-SRV-07 | A caller MAY supply a GitHub token in the `x-github-token` header. The server SHALL use it for that request only and SHALL NOT store it. |

## 4. Tools

| Tool | Purpose |
|---|---|
| `get_review_policy` | Returns the review policy and its rule identifiers. |
| `review_text` | Reviews supplied file content. |
| `review_pull_request` | Reviews the added lines of a pull request. |
| `review_commit` | Reviews the added lines of a commit and reports signature verification. |
| `review_repository` | Audits required files, CI, signing key and the README. |
| `list_repositories` | Lists the repositories of an account. |

The policy is also published as the MCP resource `agentq://policy`.

## 5. Policy

| ID | Severity | Requirement |
|---|---|---|
| AQ-SEC-01 | error | Changes MUST NOT introduce credentials, tokens or private key material. |
| AQ-SEC-02 | error | Changes MUST NOT introduce Supabase service role keys or JWT secrets. |
| AQ-DOC-01 | error | Documentation MUST NOT contain em dashes or en dashes. |
| AQ-DOC-02 | warning | Requirements SHOULD use upper case RFC 2119 keywords unless the document declares its conventions. |
| AQ-DOC-03 | warning | Normative documents SHOULD give each requirement a stable identifier. |
| AQ-DOC-04 | warning | Sentences SHOULD NOT exceed 35 words. |
| AQ-DOC-05 | warning | Documentation SHOULD NOT use filler or hedging vocabulary. |
| AQ-DOC-06 | warning | Documentation MUST NOT make unqualified assurance claims. |
| AQ-EVD-01 | info | Quantitative claims SHOULD cite the command, seed or artefact that reproduces them. |
| AQ-HYG-01 | warning | Changes SHOULD NOT add unresolved work markers. |
| AQ-REP-01 | warning | Repositories SHOULD contain README.md, LICENSE, SECURITY.md and a CI workflow. |
| AQ-REP-02 | info | Repositories SHOULD publish a signing key as `KEYS.asc`, and commits SHOULD be signed. |

A verdict of CHANGES REQUESTED follows from any error finding, COMMENT from any warning, and APPROVE otherwise.

## 6. Integration

### 6.1 Copilot custom agent

Copy `templates/.github/agents/agent-q.agent.md` to `.github/agents/` in the target repository. Agent Q then appears in the Copilot agents list for that repository.

### 6.2 GitHub Actions

Copy `templates/.github/workflows/agent-q.yml` to `.github/workflows/`. The workflow reviews each pull request with the job's own `GITHUB_TOKEN`, maintains one review comment and fails on CHANGES REQUESTED.

### 6.3 Other MCP clients

```bash
claude mcp add --transport http agent-q https://tcaefdirgffncwfwrlbe.supabase.co/functions/v1/agent-q/mcp --header "apikey: sb_publishable_KpeKmqmnbI9lTj_eu98nzA_tGUOMtBC"
```

### 6.4 Cloudflare Workers

The same application runs as a Worker (`worker/index.ts`, `wrangler.jsonc`), served at the Worker root: `/`, `/health` and `/mcp`.

Dashboard: Workers and Pages, Create, Import a repository, select this repository. Build command: none. Deploy command: `npx wrangler deploy`. Root directory: `/`.

CLI:

```bash
npm ci
npx wrangler deploy
npx wrangler secret put AGENT_Q_TOKEN
```

### 6.4.1 Single-step AI reviewer

`POST /review` on the Worker runs one pass per request: it fetches the pull request or commit from GitHub, reads the repository's data points from Supabase with `@supabase/supabase-js`, applies the Agent Q rules, makes one Workers AI call (`@cf/meta/llama-3.1-8b-instruct-fast` by default, override with `AGENT_Q_MODEL`) and saves the result to `public.reviews`. The rule verdict is authoritative; the model only explains and prioritises.

```bash
curl -X POST https://agent-q.itsdhruvsetty.workers.dev/review \
  -H "apikey: <publishable key>" -H "content-type: application/json" \
  -d '{"owner":"whatheheckisthis","repo":"agent-q","number":1}'
```

`GET /reviews/<owner>/<repo>` returns the saved history. The Worker needs the secret `SUPABASE_SERVICE_ROLE_KEY` (`npx wrangler secret put SUPABASE_SERVICE_ROLE_KEY`); without it the review still runs but is not saved. Tables are created by `supabase/migrations/20261115000000_agent_q_reviews.sql`, with RLS on and no public policies.

### 6.5 Web console

`docs/index.html` is a browser console for the server, served as static assets by the Cloudflare Worker at https://agent-q.itsdhruvsetty.workers.dev/. It reviews pasted code, pull requests, commits and repositories, and shows the policy. Settings hold the endpoint, the gateway key and an optional GitHub token, all kept in the browser.

### 6.6 Deployment

The Supabase GitHub integration deploys `supabase/functions/agent-q` to project `tcaefdirgffncwfwrlbe` on every push to `main`.

## 7. Configuration

| Secret | Location | Effect |
|---|---|---|
| `AGENT_Q_API_KEY` | Edge Function secret | API gateway key. When set, `/mcp` requires an `apikey` header equal to the project's publishable key. |
| `AGENT_Q_TOKEN` | Edge Function secret or Worker secret | Enables private bearer authentication on `/mcp`. |
| `GITHUB_TOKEN` | Edge Function secret or Worker secret | Raises the GitHub API rate limit and allows private repositories. |
| `COPILOT_MCP_AGENT_Q_TOKEN` | Repository Agents secret | Bearer token sent by the Copilot agent. |
| `AGENT_Q_TOKEN` | Repository Actions secret | Bearer token sent by the workflow. |

## 8. Local verification

```bash
deno check supabase/functions/agent-q/index.ts
supabase functions serve agent-q
npm ci && npx wrangler dev
```

## 9. Limitations

1. The policy is lexical. It does not establish the truth of a claim, only the presence of its evidence reference.
2. Pull request review covers added lines only. Context outside the diff is not evaluated.
3. Without `GITHUB_TOKEN`, the server reads public repositories only, within the unauthenticated GitHub API rate limit.
