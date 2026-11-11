#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${VERSION:-2.0.2}"
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
NATIVE_HELPER_SHA256="$(sha256sum "$ROOT/artifacts/lidarr-hardlink-import" | cut -d ' ' -f 1)"

docker run --rm \
  -e PLUGIN_VERSION="$VERSION" \
  -e PLUGIN_ASSEMBLY_VERSION="$ASSEMBLY_VERSION" \
  -e NATIVE_HELPER_SHA256="$NATIVE_HELPER_SHA256" \
  -e HOST_UID="$(id -u)" \
  -e HOST_GID="$(id -g)" \
  -v "$ROOT:/src" \
  -v "$CACHE_VOLUME:/root/.nuget/packages" \
  -w /src \
  "$IMAGE" sh -lc '
    set -eu
    version_args="-p:PluginVersion=$PLUGIN_VERSION -p:PluginAssemblyVersion=$PLUGIN_ASSEMBLY_VERSION -p:NativeHelperSha256=$NATIVE_HELPER_SHA256"
    dotnet restore Lidarr.Plugin.AIImport.sln -m:1 --configfile NuGet.config -p:NuGetAudit=false -p:EnableAnalyzers=false $version_args
    dotnet restore tools/AssemblyReferenceVerifier/AssemblyReferenceVerifier.csproj --configfile NuGet.config -p:NuGetAudit=false
    dotnet build vendor/Lidarr/src/Lidarr.Http/Lidarr.Http.csproj -m:1 --no-restore --configuration Release --nologo \
      -p:NuGetAudit=false -p:EnableAnalyzers=false -p:AssemblyVersion=3.1.2.4913
    dotnet build src/Lidarr.Plugin.AIImport/Lidarr.Plugin.AIImport.csproj -m:1 --no-restore --no-dependencies --configuration Release --nologo \
      -p:NuGetAudit=false -p:EnableAnalyzers=false $version_args
    dotnet build tests/Lidarr.Plugin.AIImport.Tests/Lidarr.Plugin.AIImport.Tests.csproj -m:1 --no-restore --no-dependencies --configuration Release --nologo \
      -p:NuGetAudit=false -p:EnableAnalyzers=false $version_args
    dotnet test tests/Lidarr.Plugin.AIImport.Tests/Lidarr.Plugin.AIImport.Tests.csproj --no-build --no-restore --configuration Release --nologo --verbosity minimal $version_args
    dotnet test tests/Lidarr.AIImport.Companion.Tests/Lidarr.AIImport.Companion.Tests.csproj -m:1 --no-restore --configuration Release --nologo --verbosity minimal -p:NuGetAudit=false -p:EnableAnalyzers=false $version_args
    dotnet run --project tools/AssemblyReferenceVerifier/AssemblyReferenceVerifier.csproj --configuration Release --no-restore -- \
      src/Lidarr.Plugin.AIImport/bin/Release/net8.0/Lidarr.Plugin.AIImport.dll "$PLUGIN_VERSION" "$PLUGIN_ASSEMBLY_VERSION" 3.1.2.4913
    for path in \
      /src/artifacts \
      /src/src/Lidarr.Plugin.AIImport/bin /src/src/Lidarr.Plugin.AIImport/obj \
      /src/companion/Lidarr.AIImport.Companion/bin /src/companion/Lidarr.AIImport.Companion/obj \
      /src/tests/Lidarr.Plugin.AIImport.Tests/bin /src/tests/Lidarr.Plugin.AIImport.Tests/obj \
      /src/tests/Lidarr.AIImport.Companion.Tests/bin /src/tests/Lidarr.AIImport.Companion.Tests/obj \
      /src/tools/AssemblyReferenceVerifier/bin /src/tools/AssemblyReferenceVerifier/obj \
      /src/vendor/Lidarr/_output /src/vendor/Lidarr/_temp; do
      if [ -e "$path" ]; then
        chown -R "$HOST_UID:$HOST_GID" "$path"
      fi
    done
  '

cp "$ROOT/src/Lidarr.Plugin.AIImport/bin/Release/net8.0/Lidarr.Plugin.AIImport.dll" "$ROOT/artifacts/"
chmod 0555 "$ROOT/artifacts/lidarr-hardlink-import"
cp "$ROOT/LICENSE" "$ROOT/artifacts/LICENSE"

if [[ -n "$(git -C "$ROOT" status --porcelain --untracked-files=all)" ]]; then
  printf 'Uncommitted verification build; no corresponding-source URL is claimed.\n' > "$ROOT/artifacts/SOURCE.txt"
else
  commit="$(git -C "$ROOT" rev-parse HEAD)"
  printf 'Corresponding source: https://github.com/mshihadeh1/Lidarr.Plugin.AIImport/tree/%s\n' "$commit" > "$ROOT/artifacts/SOURCE.txt"
fi
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
PY
python3 "$ROOT/scripts/verify-package.py" "$ROOT/artifacts/Lidarr.Plugin.AIImport-v${VERSION}.net8.0.zip" "$VERSION"

printf 'Built %s\n' "$ROOT/artifacts/Lidarr.Plugin.AIImport-v${VERSION}.net8.0.zip"
