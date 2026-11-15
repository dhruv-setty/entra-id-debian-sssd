# Entra ID sign in for Debian hosts with SSSD

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

Each release attaches a Debian package, a source tarball and `SHA256SUMS`.

```bash
V=1.0.0
curl -LO https://github.com/whatheheckisthis/agent-q/releases/download/v$V/entra-sssd_${V}_all.deb
curl -LO https://github.com/whatheheckisthis/agent-q/releases/download/v$V/SHA256SUMS
sha256sum --check --ignore-missing SHA256SUMS
sudo apt install ./entra-sssd_${V}_all.deb

entra-sssd-register-app sssd-debian-hosts           # once per tenant
sudo entra-sssd-install entra-app.env "linux-admins" # on each host
entra-sssd-verify alex@contoso.onmicrosoft.com
```

The package installs templates under `/usr/share/entra-sssd` and recommends `sssd (>= 2.11.0)`; on Debian 12 and 13 the installer adds the forky pin before installing SSSD.

## Setup from source

```bash
# 1. Register the app (az CLI, signed in to the tenant)
./scripts/register-entra-app.sh sssd-debian-hosts

# 2. Install and configure on each host
sudo ./scripts/install-debian.sh entra-app.env "linux-admins"

# 3. Verify
./scripts/verify.sh alex@contoso.onmicrosoft.com
```

`entra-app.env` holds the client secret. It is written with mode 0600; keep it out of version control and rotate the secret before expiry.

## Repository layout

| Path | Purpose |
| --- | --- |
| `sssd/sssd.conf` | Template for the `idp` domain, rendered by the installer |
| `apt/` | forky source and pin so only SSSD packages come from forky |
| `ssh/` | sshd drop in enabling keyboard-interactive for the device code prompt |
| `scripts/` | App registration, host install, verification |
| `ldap/` | Alternative domain for users provisioned into OpenLDAP |
| `packaging/` | Builds the `.deb`, tarball and checksums (`./packaging/build.sh`) |
| `.github/workflows/release.yml` | On a `v*` tag, builds the packages and attaches them to the GitHub release |
| `.github/workflows/ci.yml` | ShellCheck, plus `sssctl config-check` against the rendered config in a Debian forky container |

## Choosing between `idp` and OpenLDAP

The `idp` provider talks to Microsoft Graph directly and needs no directory server. Use the OpenLDAP domain in `ldap/` when hosts must work while offline from Entra ID, when software needs LDAP binds, or when POSIX attributes must be set explicitly. In that design an identity sync provisions Entra users and groups into OpenLDAP, and SSSD reads OpenLDAP over LDAPS.

## Security notes

- The client secret grants tenant wide read of users and group membership. Store it only in `/etc/sssd/sssd.conf` (root, 0600) and prefer a certificate credential once SSSD supports one for this provider.
- `cache_credentials = false`: every login goes to Entra, so revoked accounts lose access at the next sign in.
- The ID mapping can, in rare cases, assign the same ID to two objects; only the first one resolved is available. Keep `idmap_range_*` identical across hosts.

## References

- sssd-idp(5): https://manpages.debian.org/testing/sssd-common/sssd-idp.5.en.html
- SSSD 2.11.0 release notes: https://sssd.io/release-notes/sssd-2.11.0.html
- SSSD IdP introduction: https://sssd.io/docs/idp/idp-introduction.html

## History

Versions up to `v0.3.0` of this repository were Agent Q, a pull request reviewer on Cloudflare Workers and Supabase. That code remains at the `v0.3.0` tag and release.
