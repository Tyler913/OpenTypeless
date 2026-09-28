#!/bin/zsh
# One-time, for the maintainer: creates the self-signed certificate that GitHub Actions signs macOS releases with.
#
# Why: macOS ties Accessibility / Microphone permission to the app's signature. Ad-hoc signatures change with every
# build, so after each update users would have to grant permission again. Signing every release with the same
# certificate keeps the permissions across updates, and lets the in-app updater refuse an update that wasn't signed
# with it. (Gatekeeper still warns on the very first install, as it does for any app not notarized by Apple.)
#
# Writes OpenTypeless-Release.p12 to the folder you pass (default: the current folder) and prints the two values to
# add as repository secrets. Keep the .p12 somewhere safe and private: it is the release signing key. Losing it
# means users re-grant permissions once after the release signed with a new one.
set -euo pipefail

NAME="OpenTypeless Release"
OUT_DIR="${1:-.}"
P12="$OUT_DIR/OpenTypeless-Release.p12"
[[ -e "$P12" ]] && { echo "✗ $P12 already exists"; exit 1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

cat > "$TMP/cert.cnf" <<CNF
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
CNF

PASSWORD="$(openssl rand -hex 24)"
openssl req -x509 -newkey rsa:2048 -nodes -days 7300 \
    -keyout "$TMP/key.pem" -out "$TMP/cert.pem" -config "$TMP/cert.cnf" 2>/dev/null
# -legacy keeps the .p12 readable by macOS `security import`; older openssl builds don't know the flag.
openssl pkcs12 -export -legacy -inkey "$TMP/key.pem" -in "$TMP/cert.pem" \
    -out "$P12" -passout "pass:$PASSWORD" -name "$NAME" 2>/dev/null \
  || openssl pkcs12 -export -inkey "$TMP/key.pem" -in "$TMP/cert.pem" \
    -out "$P12" -passout "pass:$PASSWORD" -name "$NAME"
chmod 600 "$P12"

cat <<INFO
✓ Created $P12 ("$NAME", valid for 20 years)

Add two repository secrets on GitHub (Settings → Secrets and variables → Actions → New repository secret):

  MACOS_SIGNING_CERTIFICATE            the output of:  base64 -i "$P12" | pbcopy
  MACOS_SIGNING_CERTIFICATE_PASSWORD   $PASSWORD

or with the GitHub CLI:

  base64 -i "$P12" | gh secret set MACOS_SIGNING_CERTIFICATE
  gh secret set MACOS_SIGNING_CERTIFICATE_PASSWORD --body '$PASSWORD'

Every release tag from then on is signed with it.
INFO
