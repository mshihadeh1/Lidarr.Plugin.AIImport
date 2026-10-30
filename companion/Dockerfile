FROM alpine@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc AS native
RUN apk add --no-cache gcc=13.2.1_git20240309-r1 musl-dev=1.2.5-r4 linux-headers=6.6-r0
COPY native/lidarr-hardlink-import.c /src/lidarr-hardlink-import.c
RUN cc -std=c11 -Os -Wall -Wextra -Werror -static -fPIE -pie /src/lidarr-hardlink-import.c -o /lidarr-hardlink-import \
    && strip /lidarr-hardlink-import

FROM mcr.microsoft.com/dotnet/sdk@sha256:ff39a765f04619c8d146796fc09b38499ae5984f4a2a4c3a0a57d0022855daee AS build
ARG COMPANION_VERSION=0.0.0-local
ARG COMPANION_ASSEMBLY_VERSION=0.0.0.0
WORKDIR /src
COPY global.json NuGet.config ./
COPY companion/Lidarr.AIImport.Companion/Lidarr.AIImport.Companion.csproj companion/Lidarr.AIImport.Companion/
RUN dotnet restore companion/Lidarr.AIImport.Companion/Lidarr.AIImport.Companion.csproj --configfile NuGet.config -p:NuGetAudit=false -p:EnableAnalyzers=false \
    -p:Version="$COMPANION_VERSION" -p:PackageVersion="$COMPANION_VERSION" -p:AssemblyVersion="$COMPANION_ASSEMBLY_VERSION"
COPY companion/Lidarr.AIImport.Companion/ companion/Lidarr.AIImport.Companion/
RUN dotnet publish companion/Lidarr.AIImport.Companion/Lidarr.AIImport.Companion.csproj --configuration Release --no-restore --output /app -p:NuGetAudit=false -p:EnableAnalyzers=false \
    -p:Version="$COMPANION_VERSION" -p:PackageVersion="$COMPANION_VERSION" -p:AssemblyVersion="$COMPANION_ASSEMBLY_VERSION" \
    -p:FileVersion="$COMPANION_ASSEMBLY_VERSION" -p:InformationalVersion="$COMPANION_VERSION"

FROM mcr.microsoft.com/dotnet/aspnet@sha256:63a1e42746a20795273c219f1577a8826644f12f67d829e818d8ccec92915493
LABEL org.opencontainers.image.source="https://github.com/mshihadeh1/Lidarr.Plugin.AIImport" \
      org.opencontainers.image.licenses="GPL-3.0-only"
WORKDIR /app
COPY --from=build /app ./
COPY --from=native /lidarr-hardlink-import /app/lidarr-hardlink-import
COPY LICENSE /licenses/LICENSE
RUN chmod 0555 /app/lidarr-hardlink-import \
    && mkdir -p /data && chown "$APP_UID" /data && chmod 0700 /data
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Lidarr.AIImport.Companion.dll"]
