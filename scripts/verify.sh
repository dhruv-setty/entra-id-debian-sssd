#!/usr/bin/env bash
# Confirm SSSD resolves an Entra user and its groups.
set -euo pipefail
U="$1"
sssctl domain-status entra
getent passwd "$U"
id "$U"
sssctl user-checks -a acct "$U"
