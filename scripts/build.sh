#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${VERSION:-1.0.0-rc2}"
IMAGE="${DOTNET_IMAGE:-mcr.microsoft.com/dotnet/sdk@sha256:ff39a765f04619c8d146796fc09b38499ae5984f4a2a4c3a0a57d0022855daee}"
CACHE_VOLUME="${NUGET_CACHE_VOLUME:-lidarr-ai-import-nuget}"

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  printf 'Invalid VERSION: %s\n' "$VERSION" >&2
  exit 2
fi
BASE_VERSION="${VERSION%%-*}"
ASSEMBLY_VERSION="${BASE_VERSION}.0"

if [[ "${ALLOW_DIRTY_BUILD:-0}" != "1" ]] && [[ -n "$(git -C "$ROOT" status --porcelain --untracked-files=all)" ]]; then
  printf 'Refusing to package a dirty source tree; commit all release inputs first (or set ALLOW_DIRTY_BUILD=1 for local verification).\n' >&2
  exit 2
fi

bash "$ROOT/tests/native-hardlink-helper-tests.sh"

if [[ ! -f "$ROOT/vendor/Lidarr/src/NzbDrone.Core/Lidarr.Core.csproj" ]]; then
  git -C "$ROOT" submodule update --init --recursive
fi

rm -rf "$ROOT/artifacts"
mkdir -p "$ROOT/artifacts"
bash "$ROOT/scripts/build-native-helper.sh" "$ROOT/artifacts/lidarr-hardlink-import"

docker run --rm \
  -e PLUGIN_VERSION="$VERSION" \
  -e PLUGIN_ASSEMBLY_VERSION="$ASSEMBLY_VERSION" \
  -e HOST_UID="$(id -u)" \
  -e HOST_GID="$(id -g)" \
  -v "$ROOT:/src" \
  -v "$CACHE_VOLUME:/root/.nuget/packages" \
  -w /src \
  "$IMAGE" sh -lc '
    set -eu
    version_args="-p:Version=$PLUGIN_VERSION -p:PackageVersion=$PLUGIN_VERSION -p:AssemblyVersion=$PLUGIN_ASSEMBLY_VERSION -p:FileVersion=$PLUGIN_ASSEMBLY_VERSION -p:InformationalVersion=$PLUGIN_VERSION"
    dotnet restore Lidarr.Plugin.AIImport.sln --configfile NuGet.config -p:NuGetAudit=false -p:EnableAnalyzers=false $version_args
    dotnet test Lidarr.Plugin.AIImport.sln --no-restore --configuration Release --nologo --verbosity minimal -p:NuGetAudit=false -p:EnableAnalyzers=false
    dotnet build src/Lidarr.Plugin.AIImport/Lidarr.Plugin.AIImport.csproj --configuration Release --target:Rebuild --no-restore --nologo -p:NuGetAudit=false -p:EnableAnalyzers=false $version_args
    dotnet publish companion/Lidarr.AIImport.Companion/Lidarr.AIImport.Companion.csproj --configuration Release --no-restore --nologo --output /src/artifacts/companion -p:NuGetAudit=false -p:EnableAnalyzers=false $version_args
    chown -R "$HOST_UID:$HOST_GID" /src/artifacts
  '

cp "$ROOT/src/Lidarr.Plugin.AIImport/bin/Release/net8.0/Lidarr.Plugin.AIImport.dll" "$ROOT/artifacts/"
chmod 0555 "$ROOT/artifacts/lidarr-hardlink-import"
cp "$ROOT/LICENSE" "$ROOT/artifacts/LICENSE"
cp "$ROOT/scripts/ai-importctl.py" "$ROOT/artifacts/ai-importctl.py"
chmod 0555 "$ROOT/artifacts/ai-importctl.py"
commit="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || printf 'uncommitted-source-tree')"
printf 'Corresponding source: https://github.com/mshihadeh1/Lidarr.Plugin.AIImport/tree/%s\n' "$commit" > "$ROOT/artifacts/SOURCE.txt"
python3 - "$ROOT/artifacts" "$VERSION" <<'PY'
import hashlib
import pathlib
import sys
import zipfile

artifacts = pathlib.Path(sys.argv[1])
version = sys.argv[2]
archive = artifacts / f"Lidarr.Plugin.AIImport-v{version}.net8.0.zip"
with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as output:
    for name in ("Lidarr.Plugin.AIImport.dll", "lidarr-hardlink-import", "LICENSE", "SOURCE.txt"):
        output.write(artifacts / name, name)
digest = hashlib.sha256(archive.read_bytes()).hexdigest()
(archive.with_suffix(archive.suffix + ".sha256")).write_text(f"{digest}  {archive.name}\n", encoding="utf-8")

companion_archive = artifacts / f"Lidarr.AIImport.Companion-v{version}.linux-x64.zip"
with zipfile.ZipFile(companion_archive, "w", compression=zipfile.ZIP_DEFLATED) as output:
    for path in sorted((artifacts / "companion").rglob("*")):
        if path.is_file():
            output.write(path, pathlib.Path("companion") / path.relative_to(artifacts / "companion"))
    for name in ("lidarr-hardlink-import", "ai-importctl.py", "LICENSE", "SOURCE.txt"):
        output.write(artifacts / name, name)
digest = hashlib.sha256(companion_archive.read_bytes()).hexdigest()
(companion_archive.with_suffix(companion_archive.suffix + ".sha256")).write_text(
    f"{digest}  {companion_archive.name}\n", encoding="utf-8")
PY

printf 'Built %s\n' "$ROOT/artifacts/Lidarr.Plugin.AIImport-v${VERSION}.net8.0.zip"
