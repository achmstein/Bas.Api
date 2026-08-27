#!/usr/bin/env bash
#
# Registers a partner from the public key they sent, and proves it took.
#
#   ./register-partner.sh mygigsters "MyGigsters" ./bas-signing.pub
#
# Needs BAS_ADMIN_KEY in the environment - the x-admin-key value from the deployment.
# Optionally BAS_URL, which defaults to production.
#
# Registration is deliberately not automated any further than this. Adding a partner means handing
# a platform the ability to assert who its workers are, so it should be a thing a person does on
# purpose, once, with the key in front of them.

set -euo pipefail

CLIENT_ID="${1:-}"
NAME="${2:-}"
KEY_FILE="${3:-}"
SCOPES="${4:-bas:read bas:write profile:write}"
BAS_URL="${BAS_URL:-https://bas.nighttax.com.au}"

if [[ -z "$CLIENT_ID" || -z "$NAME" || -z "$KEY_FILE" ]]; then
    echo "usage: $0 <client-id> <display name> <public-key.pub> [scopes]" >&2
    echo "example: $0 mygigsters \"MyGigsters\" ./bas-signing.pub" >&2
    exit 64
fi

if [[ -z "${BAS_ADMIN_KEY:-}" ]]; then
    echo "BAS_ADMIN_KEY is not set. It is the x-admin-key value from the deployment." >&2
    exit 78
fi

if [[ ! -f "$KEY_FILE" ]]; then
    echo "No such file: $KEY_FILE" >&2
    exit 66
fi

# The single most likely mistake on their side, and the one worth catching before it reaches the
# database: sending the private half instead of the public one.
if grep -q "PRIVATE KEY" "$KEY_FILE"; then
    echo "REFUSING: $KEY_FILE contains a PRIVATE key." >&2
    echo "Ask them for the .pub file. Their private key must never leave their servers - and if" >&2
    echo "they have already sent it, it is compromised and they need to generate a new pair." >&2
    exit 65
fi

if ! grep -q "BEGIN PUBLIC KEY" "$KEY_FILE"; then
    echo "REFUSING: $KEY_FILE does not look like a PEM public key." >&2
    exit 65
fi

echo "Registering '$CLIENT_ID' ($NAME) at $BAS_URL"
echo "  key fingerprint: $(openssl pkey -pubin -in "$KEY_FILE" -outform DER 2>/dev/null | openssl dgst -sha256 | awk '{print $2}' | cut -c1-16)"
echo "  scopes:          $SCOPES"
echo

# JSON built with awk rather than python or jq, so this runs on whatever machine is to hand.
# A PEM contains only base64, dashes and newlines - no quotes or backslashes - so turning each
# newline into the two characters backslash-n is the whole of the escaping it needs.
pem_json() { awk '{ printf "%s\\n", $0 }' "$1"; }

BODY=$(cat <<JSON
{"clientId":"$CLIENT_ID","name":"$NAME","publicKeyPem":"$(pem_json "$KEY_FILE")","allowedScopes":"$SCOPES"}
JSON
)

STATUS=$(curl -s -o /tmp/register-partner.out -w "%{http_code}" \
    -X POST "$BAS_URL/admin/v1/partners" \
    -H "x-admin-key: $BAS_ADMIN_KEY" \
    -H "Content-Type: application/json" \
    -d "$BODY")

case "$STATUS" in
    200|201)
        echo "Registered."
        cat /tmp/register-partner.out
        echo
        ;;
    409)
        echo "That client id already exists. To replace their key instead:" >&2
        echo "  curl -X PUT $BAS_URL/admin/v1/partners/$CLIENT_ID/key \\" >&2
        echo "    -H \"x-admin-key: \$BAS_ADMIN_KEY\" -H 'Content-Type: application/json' \\" >&2
        echo "    -d '{\"publicKeyPem\":\"'\"\$(awk '{ printf \"%s\\n\", \$0 }' $KEY_FILE)\"'\"}'" >&2
        exit 1
        ;;
    *)
        echo "Failed with HTTP $STATUS:" >&2
        cat /tmp/register-partner.out >&2
        exit 1
        ;;
esac

rm -f /tmp/register-partner.out

echo
echo "Send them:"
echo "  - docs/bas-for-mygigsters.html   (print to PDF, for the non-technical reader)"
echo "  - docs/partner-integration.md    (their engineers)"
echo "  - scripts/partner-selftest.mjs   (proves the key works before they build anything)"
echo "  - client id: $CLIENT_ID"
