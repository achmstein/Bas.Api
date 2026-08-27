#!/usr/bin/env bash
#
# FOR THE PARTNER. Run once. Makes the key pair that identifies your platform to us.
#
#   ./generate-partner-key.sh
#
# It writes two files and tells you which one to send.

set -euo pipefail

PRIVATE="bas-signing.key"
PUBLIC="bas-signing.pub"

if [[ -f "$PRIVATE" ]]; then
    echo "$PRIVATE already exists. Not overwriting it." >&2
    echo "If you genuinely want a new pair, move the old one aside first - anything signed with" >&2
    echo "it stops working the moment we register the replacement." >&2
    exit 1
fi

openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$PRIVATE" 2>/dev/null
openssl pkey -in "$PRIVATE" -pubout -out "$PUBLIC" 2>/dev/null
chmod 600 "$PRIVATE"

cat <<EOF

Done.

  $PUBLIC   Send us this one. It is not a secret - email is fine.
  $PRIVATE  Keep this. Put it in your secret manager as BAS_SIGNING_KEY.

The private key stays on your servers. It must never reach a browser or a mobile
app bundle: anyone holding it can request a token for any of your users.

There is no password or API key in this integration. You sign, we verify with the
public half, and nothing secret ever crosses between us.

--- $PUBLIC ---
EOF

cat "$PUBLIC"
