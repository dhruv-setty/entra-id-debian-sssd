#!/usr/bin/env bash
# Install SSSD with the idp provider on Debian and render sssd.conf.
# Usage: sudo ./install-debian.sh entra-app.env "<allowed Entra group>"
#        sudo entra-sssd-install entra-app.env "<allowed Entra group>"   (from the .deb)
set -euo pipefail
ENV_FILE="$1"; ALLOWED_GROUP="$2"
[ "$(id -u)" -eq 0 ] || { echo "run as root"; exit 1; }
# shellcheck disable=SC1090
. "$ENV_FILE"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
[ -f "$HERE/sssd/sssd.conf" ] || HERE=/usr/share/entra-sssd
# shellcheck source=/dev/null
. /etc/os-release

if [ "$VERSION_CODENAME" = "bookworm" ] || [ "$VERSION_CODENAME" = "trixie" ]; then
  echo "Debian $VERSION_CODENAME ships SSSD older than 2.11; pinning SSSD from forky."
  install -m 0644 "$HERE/apt/forky.sources" /etc/apt/sources.list.d/forky.sources
  install -m 0644 "$HERE/apt/forky-sssd.pref" /etc/apt/preferences.d/forky-sssd
fi
apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y sssd sssd-idp libnss-sss libpam-sss sssd-tools

v=$(dpkg-query -W -f='${Version}' sssd-common | cut -d- -f1)
dpkg --compare-versions "$v" ge 2.11.0 || { echo "SSSD $v lacks the idp provider"; exit 1; }

sed -e "s|__TENANT_ID__|$TENANT_ID|g" -e "s|__CLIENT_ID__|$CLIENT_ID|g" \
    -e "s|__CLIENT_SECRET__|$CLIENT_SECRET|g" -e "s|__ALLOWED_GROUP__|$ALLOWED_GROUP|g" \
    "$HERE/sssd/sssd.conf" > /etc/sssd/sssd.conf
chown root:root /etc/sssd/sssd.conf; chmod 0600 /etc/sssd/sssd.conf

pam-auth-update --enable sss mkhomedir --force
install -m 0644 "$HERE/ssh/50-entra-device-code.conf" /etc/ssh/sshd_config.d/50-entra-device-code.conf
sssctl config-check
systemctl enable --now sssd
systemctl reload ssh || systemctl reload sshd || true
echo "Done. Verify with: scripts/verify.sh <user@tenant.onmicrosoft.com>"
