#!/bin/zsh
# One-time: creates a self-signed code-signing identity "OpenTypeless Dev" in your login keychain.
#
# Why: macOS ties Accessibility / Microphone permission to the app's code signature. Ad-hoc signatures
# change on every build, so permissions would reset after each rebuild. A stable local identity fixes it.
# macOS will ask for your login password once to trust the certificate for code signing.
set -euo pipefail

NAME="OpenTypeless Dev"
if security find-identity -v -p codesigning | grep -q "$NAME"; then
    echo "✓ \"$NAME\" already exists"
    exit 0
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

cat > "$TMP/cert.cnf" <<EOF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $NAME
[ext]
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
basicConstraints = critical, CA:false
EOF

openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
    -keyout "$TMP/key.pem" -out "$TMP/cert.pem" -config "$TMP/cert.cnf" 2>/dev/null
openssl pkcs12 -export -legacy -inkey "$TMP/key.pem" -in "$TMP/cert.pem" \
    -out "$TMP/id.p12" -passout pass:opentypeless -name "$NAME" 2>/dev/null \
  || openssl pkcs12 -export -inkey "$TMP/key.pem" -in "$TMP/cert.pem" \
    -out "$TMP/id.p12" -passout pass:opentypeless -name "$NAME"

KEYCHAIN="$HOME/Library/Keychains/login.keychain-db"
security import "$TMP/id.p12" -k "$KEYCHAIN" -P opentypeless -T /usr/bin/codesign
security add-trusted-cert -r trustRoot -p codeSign -k "$KEYCHAIN" "$TMP/cert.pem"

echo "✓ Created \"$NAME\". Rebuild with scripts/build-app.sh --install"
