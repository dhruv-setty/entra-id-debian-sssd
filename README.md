# Agent Q

Agent Q is a Model Context Protocol (MCP) review server for repository governance. It applies a deterministic policy to pull requests, commits and repositories, and returns findings by rule identifier.

## 1. Scope

| Item | Value |
|---|---|
| Runtime | Supabase Edge Functions (Deno) |
| Protocol | MCP, Streamable HTTP transport, stateless |
| Libraries | `@modelcontextprotocol/sdk` 1.32.2, Hono 4 |
| Clients | GitHub Copilot custom agent, GitHub Actions, any MCP client |
| Endpoint | `https://gsluekbphbusnaguqeol.supabase.co/functions/v1/agent-q/mcp` |

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
| AQ-HYG-01 | warning | Changes SHOULD NOT add unresolved TODO, FIXME or XXX markers. |
| AQ-REP-01 | warning | Repositories SHOULD contain README.md, LICENSE, SECURITY.md and a CI workflow. |
| AQ-REP-02 | info | Repositories SHOULD publish a signing key as `KEYS.asc`, and commits SHOULD be signed. |

A verdict of CHANGES REQUESTED follows from any error finding, COMMENT from any warning, and APPROVE otherwise.

## 6. Integration

### 6.1 Copilot custom agent

Copy `templates/.github/agents/agent-q.agent.md` to `.github/agents/` in the target repository. Agent Q then appears in the Copilot agents list for that repository.

### 6.2 GitHub Actions

Copy `templates/.github/workflows/agent-q.yml` to `.github/workflows/`. The workflow reviews each pull request, maintains one review comment and fails on CHANGES REQUESTED.

### 6.3 Other MCP clients

```bash
claude mcp add --transport http agent-q https://gsluekbphbusnaguqeol.supabase.co/functions/v1/agent-q/mcp
```

## 7. Configuration

| Secret | Location | Effect |
|---|---|---|
| `AGENT_Q_TOKEN` | Edge Function secret | Enables bearer authentication on `/mcp`. |
| `GITHUB_TOKEN` | Edge Function secret | Raises the GitHub API rate limit and allows private repositories. |
| `COPILOT_MCP_AGENT_Q_TOKEN` | Repository Agents secret | Bearer token sent by the Copilot agent. |
| `AGENT_Q_TOKEN` | Repository Actions secret | Bearer token sent by the workflow. |

## 8. Local verification

```bash
deno check supabase/functions/agent-q/index.ts
supabase functions serve agent-q
```

## 9. Limitations

1. The policy is lexical. It does not establish the truth of a claim, only the presence of its evidence reference.
2. Pull request review covers added lines only. Context outside the diff is not evaluated.
3. Without `GITHUB_TOKEN`, the server reads public repositories only, within the unauthenticated GitHub API rate limit.
