# Intent-Auditable Trust Object V2

Entra ID sign in for Debian hosts with SSSD, managed by `iato`, a C# (.NET 8) command line tool.

Debian hosts authenticate Microsoft Entra ID users through SSSD's identity provider (`idp`) back end. SSSD reads users and groups from Microsoft Graph, signs users in with the OAuth 2.0 Device Authorization Grant (RFC 8628), and derives consistent POSIX UIDs and GIDs from Entra object IDs, so no ID mapping is managed by hand.

## How it works

1. An Entra ID app registration gives SSSD application permissions to read users and group membership, and allows public client flows for device code sign in.
2. SSSD runs with `id_provider = idp` and `idp_type = entra_id`. `getent` and `id` resolve Entra users and groups.
3. At login, `pam_sss` shows a verification URL and code. The user signs in on any browser, Conditional Access and MFA apply, and SSSD completes the login once Entra confirms.
4. `access_provider = simple` limits logins to one Entra group.

## Requirements

| Item | Detail |
| --- | --- |
| SSSD | 2.11.0 or later. Debian 12 ships 2.8.2 and Debian 13 ships 2.10.1, so this repository pins SSSD packages from Debian forky (2.13). Debian forky and sid work as is. |
| Packages | `sssd`, `sssd-idp`, `libnss-sss`, `libpam-sss`, `sssd-tools` |
| Entra ID | Rights to create an app registration and grant admin consent |
| Graph permissions | Application: `User.Read.All`, `GroupMember.Read.All`. Delegated: `openid`, `profile`, `email` |

## Install from a release

Releases are cut from the `release/2.x` branch. Each one attaches `iato_<version>_amd64.deb`, `iato_<version>_arm64.deb`, standalone `iato-linux-x64` and `iato-linux-arm64` binaries, a source tarball and `SHA256SUMS`.

```console
V=2.0.0
curl -LO https://github.com/whatheheckisthis/Intent-Auditable-Trust-Object-V2/releases/download/v$V/iato_${V}_amd64.deb
curl -LO https://github.com/whatheheckisthis/Intent-Auditable-Trust-Object-V2/releases/download/v$V/SHA256SUMS
sha256sum --check --ignore-missing SHA256SUMS
sudo apt install ./iato_${V}_amd64.deb

iato register-app --name sssd-debian-hosts --out entra-app.env  # once per tenant
sudo iato install --env entra-app.env --group linux-admins      # on each host
iato verify alex@contoso.onmicrosoft.com
```

The binary is self contained, so hosts need no .NET runtime. Templates are compiled into it. On Debian 12 and 13, `iato install` adds the forky pin before installing SSSD.

## Commands

| Command | What it does |
| --- | --- |
| `iato register-app` | Creates the Entra app registration through Microsoft Graph, grants admin consent, and writes `entra-app.env` (mode 0600). Uses `IATO_GRAPH_TOKEN` or `az account get-access-token`. |
| `iato render` | Renders `sssd.conf` from the embedded template. Rejects missing or multi line values. |
| `iato check` | Checks a config: placeholders, duplicate keys, required `idp` settings, https endpoints, idmap range, `config_file_version`. `--host` also checks the SSSD version, `libsss_idp.so` and the file mode. |
| `iato install` | Pins SSSD from forky where needed, installs packages, writes the config, enables `pam_sss` and the sshd drop in, and starts SSSD only after the checks pass. |
| `iato verify` | Runs `sssctl domain-status`, `getent`, `id` and `sssctl user-checks` for a user. |

## Build from source

```console
dotnet test
dotnet publish src/Iato -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
```

## Branches

| Branch | Holds |
| --- | --- |
| `main` | The `iato` CLI, the `idp` domain template, tests and CI |
| `openldap` | `main` plus an OpenLDAP domain (`iato render --provider ldap`) for hosts that must work offline from Entra ID or need LDAP binds |
| `release/2.x` | `main` plus Debian packaging and the release workflow; version tags are cut here |

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/Iato/` | The CLI (.NET 8) |
| `tests/Iato.Tests/` | xUnit tests for rendering, checks and the Graph request |
| `sssd/sssd.conf` | Template for the `idp` domain |
| `apt/` | forky source and pin so only SSSD packages come from forky |
| `ssh/` | sshd drop in enabling keyboard-interactive for the device code prompt |
| `.github/workflows/ci.yml` | Build, test, then `iato check --host` against SSSD in a Debian forky container |

## OpenLDAP domain (this branch)

```bash
# ldap.env: LDAP_URI, SEARCH_BASE, BIND_DN, BIND_PASSWORD
sudo iato install --provider ldap --env ldap.env --group cn=linux-login,ou=groups,dc=example,dc=internal
```

Use it when hosts must keep working while offline from Entra ID, when software needs LDAP binds, or when POSIX attributes must be set explicitly. An identity sync provisions Entra users and groups into OpenLDAP; SSSD reads it over LDAPS (`iato check` rejects `ldap://`). Template: `sssd/sssd-ldap.conf`.

## Security notes

- The client secret grants tenant wide read of users and group membership. Store it only in `/etc/sssd/sssd.conf` (root, 0600) and prefer a certificate credential once SSSD supports one for this provider.
- `cache_credentials = false`: every login goes to Entra, so revoked accounts lose access at the next sign in.
- The ID mapping can, in rare cases, assign the same ID to two objects; only the first one resolved is available. Keep `idmap_range_*` identical across hosts.

## References

- sssd-idp(5): https://manpages.debian.org/testing/sssd-common/sssd-idp.5.en.html
- SSSD 2.11.0 release notes: https://sssd.io/release-notes/sssd-2.11.0.html
- SSSD IdP introduction: https://sssd.io/docs/idp/idp-introduction.html

## History

This repository was named `agent-q` until v2. Versions up to `v0.3.0` were Agent Q, a pull request reviewer on Cloudflare Workers and Supabase. That code remains at the `v0.3.0` tag and release. `v1.0.0` was the first shell script version of the SSSD project.
