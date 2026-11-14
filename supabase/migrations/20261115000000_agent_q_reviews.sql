-- Agent Q: repository data points read by the reviewer, and the reviews it writes.
-- Accessed only by the Cloudflare Worker with the project's secret key; RLS is on with no policies, so anon and authenticated roles see nothing.

create table if not exists public.repo_data_points (
  repo       text        not null,
  key        text        not null,
  value      jsonb       not null default '{}'::jsonb,
  updated_at timestamptz not null default now(),
  primary key (repo, key)
);

create table if not exists public.reviews (
  id          uuid        primary key default gen_random_uuid(),
  created_at  timestamptz not null default now(),
  repo        text        not null,
  kind        text        not null check (kind in ('pull_request', 'commit', 'text')),
  target      text        not null,
  verdict     text        not null,
  errors      integer     not null default 0,
  warnings    integer     not null default 0,
  notes       integer     not null default 0,
  findings    jsonb       not null default '[]'::jsonb,
  ai_summary  text,
  model       text,
  data_points jsonb
);

create index if not exists reviews_repo_created_idx on public.reviews (repo, created_at desc);

alter table public.repo_data_points enable row level security;
alter table public.reviews enable row level security;

revoke all on public.repo_data_points from anon, authenticated;
revoke all on public.reviews from anon, authenticated;
