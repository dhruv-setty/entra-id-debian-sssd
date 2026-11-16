# Intent-Auditable Trust Object V2

This repository provides `iato`, a C# (.NET 8) command line tool that configures Debian hosts to authenticate Microsoft Entra ID users through the SSSD identity provider (`idp`) back end. SSSD reads users and groups from Microsoft Graph, signs users in with the OAuth 2.0 Device Authorization Grant (RFC 8628), and derives consistent POSIX UIDs and GIDs from Entra object IDs, so no ID mapping is managed by hand. It is not an identity provider, a directory service or a replacement for SSSD; it renders, checks and installs SSSD configuration and verifies the result.

The key words MUST, MUST NOT, SHALL, SHOULD and MAY in this document are to be interpreted as described in BCP 14 (RFC 2119 and RFC 8174).

## Scope

In scope:

- Creating the Entra ID app registration through Microsoft Graph (`iato register-app`).
- Rendering and checking `sssd.conf` for the `idp` domain (`iato render`, `iato check`).
- Installing and enabling SSSD, `pam_sss` and the sshd drop in on Debian hosts (`iato install`).
- Verifying that a user resolves and may log in (`iato verify`).

Out of scope:

- Distributions other than Debian.
- SSSD versions earlier than 2.11.0, which have no `idp` provider.
- The OpenLDAP domain and Debian packaging, which are held on separate branches (see Branches).

## How it works

1. An Entra ID app registration gives SSSD application permissions to read users and group membership, and allows public client flows for device code sign in.
2. SSSD runs with `id_provider = idp` and `idp_type = entra_id`. `getent` and `id` resolve Entra users and groups.
3. At login, `pam_sss` shows a verification URL and code. The user signs in on any browser, Conditional Access and MFA apply, and SSSD completes the login once Entra confirms.
4. `access_provider = simple` limits logins to one Entra group.

## Requirements

| Item | Detail |
| --- | --- |
| SSSD | 2.11.0 or later MUST be installed. Debian 12 ships 2.8.2 and Debian 13 ships 2.10.1, so this repository pins SSSD packages from Debian forky (2.13). Debian forky and sid ship a suitable version as is. |
| Packages | `sssd`, `sssd-idp`, `libnss-sss`, `libpam-sss`, `sssd-tools` |
| Entra ID | Rights to create an app registration and grant admin consent |
| Graph permissions | Application: `User.Read.All`, `GroupMember.Read.All`. Delegated: `openid`, `profile`, `email` |
| Build | .NET 8 SDK, only when building from source |

The released binary is self contained, so hosts do not need a .NET runtime. Templates are compiled into it.

## Installation

### Install from a release

Releases are cut from the `release/2.x` branch. Each one attaches `iato_<version>_amd64.deb`, `iato_<version>_arm64.deb`, standalone `iato-linux-x64` and `iato-linux-arm64` binaries, a source tarball and `SHA256SUMS`. Downloads SHOULD be checked against `SHA256SUMS` before installation.

```console
V=2.0.0
curl -LO https://github.com/whatheheckisthis/Intent-Auditable-Trust-Object-V2/releases/download/v$V/iato_${V}_amd64.deb
curl -LO https://github.com/whatheheckisthis/Intent-Auditable-Trust-Object-V2/releases/download/v$V/SHA256SUMS
sha256sum --check --ignore-missing SHA256SUMS
sudo apt install ./iato_${V}_amd64.deb
```

### Build from source

```console
dotnet test
dotnet publish src/Iato -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
```

## Usage

```console
iato register-app --name sssd-debian-hosts --out entra-app.env  # once per tenant
sudo iato install --env entra-app.env --group linux-admins      # on each host
iato verify alex@contoso.onmicrosoft.com
```

`iato register-app` runs once per tenant. `iato install` runs on each host and MUST run as root. On Debian 12 and 13, `iato install` adds the forky pin before installing SSSD.

| Command | Behaviour |
| --- | --- |
| `iato register-app` | Creates the Entra app registration through Microsoft Graph, grants admin consent, and writes `entra-app.env` (mode 0600). Uses `IATO_GRAPH_TOKEN` or `az account get-access-token`. |
| `iato render` | Renders `sssd.conf` from the embedded template. Rejects missing or multi line values. |
| `iato check` | Checks a config: placeholders, duplicate keys, required `idp` settings, https endpoints, idmap range, `config_file_version`. `--host` also checks the SSSD version, `libsss_idp.so` and the file mode. |
| `iato install` | Pins SSSD from forky where needed, installs packages, writes the config, enables `pam_sss` and the sshd drop in, and starts SSSD only after the checks pass. |
| `iato verify` | Runs `sssctl domain-status`, `getent`, `id` and `sssctl user-checks` for a user. |
| `iato version` | Prints the tool version. |

## Configuration

- `sssd/sssd.conf` is the template for the `idp` domain. `iato render` and `iato install` fill it from `entra-app.env` and the `--group` value.
- `apt/` holds the forky source and pin so that only SSSD packages come from forky.
- `ssh/` holds the sshd drop in that enables keyboard-interactive authentication for the device code prompt.
- The client secret grants tenant wide read of users and group membership. It MUST be stored only in `/etc/sssd/sssd.conf` (root, 0600). A certificate credential SHOULD be preferred once SSSD supports one for this provider.
- `cache_credentials = false`: every login goes to Entra, so revoked accounts lose access at the next sign in.
- `idmap_range_*` values MUST be identical across hosts.

## Verification

- `dotnet test` runs the xUnit tests in `tests/Iato.Tests/`, which cover rendering, checks and the Graph request.
- `.github/workflows/ci.yml` builds and tests on each push and pull request, then runs `iato check --host` against SSSD in a Debian forky container. Continuous integration does not cover Debian 12, Debian 13 or sid hosts.
- On a configured host, `iato verify <user@tenant>` MAY be used to confirm that a user resolves and may log in.

## Known limitations

- The ID mapping can, in rare cases, assign the same ID to two objects; only the first one resolved is available.
- The client secret is the only credential type supported for this provider at present.
- On Debian 12 and 13, SSSD packages are taken from Debian forky through an apt pin rather than from the installed release.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/Iato/` | The CLI (.NET 8) |
| `tests/Iato.Tests/` | xUnit tests for rendering, checks and the Graph request |
| `sssd/sssd.conf` | Template for the `idp` domain |
| `apt/` | forky source and pin so only SSSD packages come from forky |
| `ssh/` | sshd drop in enabling keyboard-interactive for the device code prompt |
| `.github/workflows/ci.yml` | Build, test, then `iato check --host` against SSSD in a Debian forky container |

## Branches

| Branch | Holds |
| --- | --- |
| `main` | The `iato` CLI, the `idp` domain template, tests and CI |
| `openldap` | `main` plus an OpenLDAP domain (`iato render --provider ldap`) for hosts that must work offline from Entra ID or need LDAP binds |
| `release/2.x` | `main` plus Debian packaging and the release workflow; version tags are cut here |

## References

- [sssd-idp(5)](https://manpages.debian.org/testing/sssd-common/sssd-idp.5.en.html)
- [SSSD 2.11.0 release notes](https://sssd.io/release-notes/sssd-2.11.0.html)
- [SSSD IdP introduction](https://sssd.io/docs/idp/idp-introduction.html)

## History

This repository was named `agent-q` until v2. Versions up to `v0.3.0` were Agent Q, a pull request reviewer on Cloudflare Workers and Supabase. That code remains at the `v0.3.0` tag and release. `v1.0.0` was the first release of the SSSD project.

## Security

Security reporting and commit signature verification are described in [SECURITY.md](SECURITY.md); the maintainer signing key is published in [KEYS.asc](KEYS.asc).

## License

This project is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE).
