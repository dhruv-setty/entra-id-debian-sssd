#!/usr/bin/env bash
# Build dist/entra-sssd-<v>.tar.gz, dist/entra-sssd_<v>_all.deb and SHA256SUMS.
set -euo pipefail
cd "$(dirname "$0")/.."
V="$(cat VERSION)"
OUT=dist; rm -rf "$OUT"; mkdir -p "$OUT"
FILES=(README.md LICENSE SECURITY.md VERSION sssd apt ssh ldap scripts)

# Source tarball
tar --sort=name --owner=0 --group=0 --numeric-owner --mtime='2026-11-15 00:00Z' \
  --transform "s,^,entra-sssd-$V/," -czf "$OUT/entra-sssd-$V.tar.gz" "${FILES[@]}"

# Debian package
R="$(mktemp -d)"; chmod 0755 "$R"; trap 'rm -rf "$R"' EXIT
install -d "$R/DEBIAN" "$R/usr/share/entra-sssd" "$R/usr/sbin" "$R/usr/share/doc/entra-sssd"
cp -r sssd apt ssh ldap "$R/usr/share/entra-sssd/"
install -m 0755 scripts/install-debian.sh "$R/usr/sbin/entra-sssd-install"
install -m 0755 scripts/register-entra-app.sh "$R/usr/sbin/entra-sssd-register-app"
install -m 0755 scripts/verify.sh "$R/usr/sbin/entra-sssd-verify"
install -m 0644 README.md "$R/usr/share/doc/entra-sssd/README.md"
install -m 0644 LICENSE "$R/usr/share/doc/entra-sssd/copyright"
find "$R/usr/share/entra-sssd" -type f -exec chmod 0644 {} +
sed "s/@VERSION@/$V/" packaging/control.in > "$R/DEBIAN/control"
dpkg-deb --root-owner-group -Zxz --build "$R" "$OUT/entra-sssd_${V}_all.deb" >/dev/null

(cd "$OUT" && sha256sum ./* > SHA256SUMS)
ls -l "$OUT"
