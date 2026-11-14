// Agent Q review rules. Deterministic, dependency free, runtime agnostic.

export type Severity = "error" | "warning" | "info";
export interface Finding {
  rule: string;
  severity: Severity;
  file: string;
  line: number;
  message: string;
  excerpt?: string;
}

export const POLICY = `# Agent Q Review Policy

Agent Q SHALL review every change against the rules below. Each rule has a stable identifier.

| ID | Severity | Requirement |
|---|---|---|
| AQ-SEC-01 | error | Changes MUST NOT introduce credentials, tokens or private key material. |
| AQ-SEC-02 | error | Changes MUST NOT introduce Supabase service role keys or JWT secrets. |
| AQ-DOC-01 | error | Documentation MUST NOT contain em dashes or en dashes. |
| AQ-DOC-02 | warning | Documentation SHOULD state requirements with RFC 2119 keywords in upper case (MUST, SHALL, SHOULD, MAY), unless it declares its conventions (RFC 2119, RFC 8174 or ISO/IEC Directives). |
| AQ-DOC-03 | warning | A document that states normative requirements SHOULD give each requirement a stable identifier. |
| AQ-DOC-04 | warning | Sentences SHOULD NOT exceed 35 words. |
| AQ-DOC-05 | warning | Documentation SHOULD NOT use filler or hedging vocabulary (for example: very, simply, just, basically, obviously). |
| AQ-DOC-06 | warning | Documentation MUST NOT make unqualified assurance claims (for example: production ready, guaranteed, 100% secure, bulletproof). |
| AQ-EVD-01 | info | A quantitative claim SHOULD cite the command, seed or artefact that reproduces it. |
| AQ-HYG-01 | warning | Changes SHOULD NOT add unresolved TODO, FIXME or XXX markers. |
| AQ-REP-01 | warning | A repository SHOULD contain README.md, LICENSE, SECURITY.md and a CI workflow. |
| AQ-REP-02 | info | A repository SHOULD publish its signing key as KEYS.asc. |
`;

const SECRET_PATTERNS: [RegExp, string, string][] = [
  [/-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY(?: BLOCK)?-----/, "AQ-SEC-01", "Private key material"],
  [/\bAKIA[0-9A-Z]{16}\b/, "AQ-SEC-01", "AWS access key ID"],
  [/\bgh[pousr]_[A-Za-z0-9]{36,}\b/, "AQ-SEC-01", "GitHub token"],
  [/\bgithub_pat_[A-Za-z0-9_]{60,}\b/, "AQ-SEC-01", "GitHub fine grained token"],
  [/\bsk-(?:ant-|proj-)?[A-Za-z0-9_-]{32,}\b/, "AQ-SEC-01", "API secret key"],
  [/\bxox[baprs]-[A-Za-z0-9-]{10,}\b/, "AQ-SEC-01", "Slack token"],
  [/\bre_[A-Za-z0-9]{20,}\b/, "AQ-SEC-01", "Resend API key"],
  [/\bsb_secret_[A-Za-z0-9_-]{20,}\b/, "AQ-SEC-02", "Supabase secret key"],
  [/service_role/i, "AQ-SEC-02", "Reference to a Supabase service role credential"],
];

const FILLER = /\b(very|simply|just|basically|obviously|clearly|actually|really|literally)\b/i;
const ASSURANCE = /\b(production[- ]ready|guaranteed?|100% secure|bulletproof|unhackable|military[- ]grade|fully secure)\b/i;
const RFC_LOWER = /\b(must|shall|should|may)\b(?! not be confused)/;
const RFC_UPPER = /\b(MUST|SHALL|SHOULD|MAY|REQUIRED|RECOMMENDED)\b/;
const REQ_ID = /\b[A-Z][A-Z0-9]{1,6}(?:-[A-Z0-9]{1,6})*-\d{2,3}\b/;
const NUMBER_CLAIM = /\b\d[\d,.]*\s?(?:%|MHz|GHz|cycles|ms|B\/cycle|LUT4|FF|states|probes|mismatches|x faster)\b/;
const EVIDENCE = /(seed|reproduc|command|`[^`]+`|artefact|artifact|see\s)/i;

export const isDoc = (f: string) => /\.(md|mdx|markdown|rst|adoc|txt)$/i.test(f);

function excerpt(s: string) {
  const t = s.trim();
  return t.length > 140 ? t.slice(0, 137) + "..." : t;
}

/** Review a set of lines. `lines` pairs each line's number with its text. */
export function reviewLines(file: string, lines: [number, string][]): Finding[] {
  const out: Finding[] = [];
  const doc = isDoc(file);
  let inFence = false;
  let sawUpper = false;
  let sawId = false;
  // A document that declares its conventions (RFC 2119, RFC 8174 or ISO/IEC Directives) may use lower case keywords.
  const declared = lines.some(([, l]) => /RFC\s?(2119|8174)|ISO\/IEC Directives/i.test(l));

  for (const [n, raw] of lines) {
    for (const [re, rule, label] of SECRET_PATTERNS) {
      if (re.test(raw)) out.push({ rule, severity: "error", file, line: n, message: `${label} detected. Remove it and rotate the credential.`, excerpt: "[redacted]" });
    }
    if (/\b(TODO|FIXME|XXX)\b/.test(raw)) out.push({ rule: "AQ-HYG-01", severity: "warning", file, line: n, message: "Unresolved marker added.", excerpt: excerpt(raw) });
    if (!doc) continue;

    if (/^\s*(```|~~~)/.test(raw)) { inFence = !inFence; continue; }
    if (inFence || /^\s*\|?\s*:?-{3,}/.test(raw)) continue;
    const text = raw.replace(/`[^`]*`/g, "").replace(/\]\([^)]*\)/g, "]");

    if (/[\u2013\u2014]/.test(text)) out.push({ rule: "AQ-DOC-01", severity: "error", file, line: n, message: "Em or en dash found. Use a comma, colon, full stop or parentheses.", excerpt: excerpt(raw) });
    if (RFC_UPPER.test(text) || (declared && RFC_LOWER.test(text))) sawUpper = true;
    if (REQ_ID.test(text)) sawId = true;
    const lower = text.match(RFC_LOWER);
    if (lower && !declared && !RFC_UPPER.test(text)) out.push({ rule: "AQ-DOC-02", severity: "warning", file, line: n, message: `Lower case "${lower[1]}" reads as a requirement. Write ${lower[1].toUpperCase()} if normative, or rephrase.`, excerpt: excerpt(raw) });
    const filler = text.match(FILLER);
    if (filler) out.push({ rule: "AQ-DOC-05", severity: "warning", file, line: n, message: `Filler word "${filler[1]}".`, excerpt: excerpt(raw) });
    const claim = text.match(ASSURANCE);
    if (claim) out.push({ rule: "AQ-DOC-06", severity: "warning", file, line: n, message: `Unqualified assurance claim "${claim[1]}". State the scope and the evidence instead.`, excerpt: excerpt(raw) });
    if (NUMBER_CLAIM.test(text) && !EVIDENCE.test(text) && !/^\s*\|/.test(raw)) out.push({ rule: "AQ-EVD-01", severity: "info", file, line: n, message: "Quantitative claim without a reproduction reference.", excerpt: excerpt(raw) });
    for (const s of text.split(/(?<=[.!?])\s+/)) {
      const words = s.trim().split(/\s+/).filter(Boolean).length;
      if (words > 35) { out.push({ rule: "AQ-DOC-04", severity: "warning", file, line: n, message: `Sentence of ${words} words. Split it.`, excerpt: excerpt(s) }); break; }
    }
  }
  if (doc && sawUpper && !sawId) out.push({ rule: "AQ-DOC-03", severity: "warning", file, line: lines[0]?.[0] ?? 1, message: "Normative keywords are used but no requirement identifiers were found." });
  return out;
}

export function reviewText(file: string, content: string): Finding[] {
  return reviewLines(file, content.split("\n").map((l, i) => [i + 1, l] as [number, string]));
}

/** Extract added lines with their new-file line numbers from a unified diff patch. */
export function addedLines(patch: string): [number, string][] {
  const out: [number, string][] = [];
  let n = 0;
  for (const l of patch.split("\n")) {
    const h = l.match(/^@@ -\d+(?:,\d+)? \+(\d+)/);
    if (h) { n = Number(h[1]); continue; }
    if (l.startsWith("+") && !l.startsWith("+++")) { out.push([n, l.slice(1)]); n++; }
    else if (!l.startsWith("-")) n++;
  }
  return out;
}

export function summarise(findings: Finding[]) {
  const c = { error: 0, warning: 0, info: 0 };
  for (const f of findings) c[f.severity]++;
  const verdict = c.error > 0 ? "CHANGES REQUESTED" : c.warning > 0 ? "COMMENT" : "APPROVE";
  return { verdict, counts: c };
}

export function toMarkdown(title: string, findings: Finding[]) {
  const { verdict, counts } = summarise(findings);
  const head = `## Agent Q review: ${title}\n\n**Verdict:** ${verdict} (${counts.error} errors, ${counts.warning} warnings, ${counts.info} notes)\n`;
  if (!findings.length) return head + "\nNo findings against the Agent Q policy.\n";
  const rows = findings.map((f) => `| ${f.severity} | ${f.rule} | \`${f.file}:${f.line}\` | ${f.message.replace(/\|/g, "\\|")} |`).join("\n");
  return `${head}\n| Severity | Rule | Location | Finding |\n|---|---|---|---|\n${rows}\n`;
}
