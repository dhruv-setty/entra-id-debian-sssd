#!/usr/bin/env bash
# Build dist/: iato_<v>_{amd64,arm64}.deb, iato-linux-{x64,arm64}, iato-<v>.tar.gz, SHA256SUMS.
set -euo pipefail
cd "$(dirname "$0")/.."
V="$(cat VERSION)"
OUT=dist; rm -rf "$OUT"; mkdir -p "$OUT"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

dotnet test -c Release

for pair in linux-x64:amd64 linux-arm64:arm64; do
  RID="${pair%%:*}"; ARCH="${pair##*:}"
  PUB="$(mktemp -d)"
  dotnet publish src/Iato -c Release -r "$RID" --self-contained \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none -p:ContinuousIntegrationBuild=true -o "$PUB"
  install -m 0755 "$PUB/iato" "$OUT/iato-$RID"

  R="$(mktemp -d)"; chmod 0755 "$R"
  install -d "$R/DEBIAN" "$R/usr/bin" "$R/usr/share/doc/iato"
  install -m 0755 "$PUB/iato" "$R/usr/bin/iato"
  install -m 0644 README.md "$R/usr/share/doc/iato/README.md"
  install -m 0644 LICENSE "$R/usr/share/doc/iato/copyright"
  sed -e "s/@VERSION@/$V/" -e "s/@ARCH@/$ARCH/" packaging/control.in > "$R/DEBIAN/control"
  dpkg-deb --root-owner-group -Zxz --build "$R" "$OUT/iato_${V}_${ARCH}.deb" >/dev/null
  rm -rf "$R" "$PUB"
done

git archive --format=tar.gz --prefix="iato-$V/" -o "$OUT/iato-$V.tar.gz" HEAD
(cd "$OUT" && sha256sum ./* > SHA256SUMS)
ls -l "$OUT"
