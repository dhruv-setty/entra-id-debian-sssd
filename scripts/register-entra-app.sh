#!/usr/bin/env bash
# Register the Entra ID application SSSD uses. Requires: az CLI, signed in as
# an account allowed to create apps and grant admin consent.
set -euo pipefail
NAME="${1:-sssd-debian-hosts}"
GRAPH=00000003-0000-0000-c000-000000000000
# Microsoft Graph permission IDs
USER_READ_ALL=df021288-bdef-4463-88db-98f22de89214         # Role: User.Read.All
GROUPMEMBER_READ_ALL=98830695-27a2-44f7-8c18-0c3ebc9698f6  # Role: GroupMember.Read.All
OPENID=37f7f235-527c-4136-accd-4a02d197296e                # Scope: openid
PROFILE=14dad69e-099b-42c9-810b-d002981feec1               # Scope: profile
EMAIL=64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0                 # Scope: email

TENANT_ID=$(az account show --query tenantId -o tsv)
APP_ID=$(az ad app create --display-name "$NAME" \
  --is-fallback-public-client true \
  --sign-in-audience AzureADMyOrg --query appId -o tsv)
az ad sp create --id "$APP_ID" >/dev/null

az ad app permission add --id "$APP_ID" --api "$GRAPH" --api-permissions \
  "$USER_READ_ALL=Role" "$GROUPMEMBER_READ_ALL=Role" \
  "$OPENID=Scope" "$PROFILE=Scope" "$EMAIL=Scope"
az ad app permission admin-consent --id "$APP_ID"

SECRET=$(az ad app credential reset --id "$APP_ID" --display-name sssd \
  --years 1 --query password -o tsv)

umask 077
cat > entra-app.env <<ENV
TENANT_ID=$TENANT_ID
CLIENT_ID=$APP_ID
CLIENT_SECRET=$SECRET
ENV
echo "Wrote entra-app.env (mode 0600). Rotate the secret before it expires in 1 year."
