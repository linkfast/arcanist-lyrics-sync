#!/usr/bin/env sh
# Builds the extension's package: artifacts/arcanist-lyrics-sync-<version>.zip (and its .sha256), the file administrators
# install from Arcanist's panel (Extensions → Install from URL or upload). Reproducible: the same sources give the
# same bytes.
#
#   ./pack.sh                 # needs .NET 10 and an Arcanist checkout at ../../Arcanist
#   ARCANIST_ROOT=/path/to/arcanist ./pack.sh
set -eu
root="$(cd "$(dirname "$0")" && pwd)"
cd "$root"

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -n 1)"
arcanist="${ARCANIST_ROOT:-$root/../../Arcanist}"

rm -rf artifacts/publish
dotnet publish src/Arcanist.LyricsSync -c Release -o artifacts/publish --artifacts-path artifacts/build \
    -p:ArcanistRoot="$arcanist/" -p:ContinuousIntegrationBuild=true -p:Deterministic=true
rm -f artifacts/publish/*.pdb

python3 - "$root/artifacts/publish" "$root/artifacts/arcanist-lyrics-sync-$version.zip" <<'PY'
import os, sys, zipfile
source, target = sys.argv[1], sys.argv[2]
files = sorted(os.path.relpath(os.path.join(d, f), source).replace(os.sep, "/") for d, _, fs in os.walk(source) for f in fs)
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for name in files:
        info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
        info.external_attr = 0o644 << 16
        info.compress_type = zipfile.ZIP_DEFLATED
        with open(os.path.join(source, name), "rb") as f:
            z.writestr(info, f.read())
PY

(cd artifacts && sha256sum "arcanist-lyrics-sync-$version.zip" > "arcanist-lyrics-sync-$version.zip.sha256" && cat "arcanist-lyrics-sync-$version.zip.sha256")
