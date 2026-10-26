FROM mcr.microsoft.com/dotnet/sdk@sha256:ff39a765f04619c8d146796fc09b38499ae5984f4a2a4c3a0a57d0022855daee AS build
WORKDIR /src
COPY global.json NuGet.config ./
COPY companion/Lidarr.AIImport.Companion/Lidarr.AIImport.Companion.csproj companion/Lidarr.AIImport.Companion/
RUN dotnet restore companion/Lidarr.AIImport.Companion/Lidarr.AIImport.Companion.csproj --configfile NuGet.config -p:NuGetAudit=false -p:EnableAnalyzers=false
COPY companion/Lidarr.AIImport.Companion/ companion/Lidarr.AIImport.Companion/
RUN dotnet publish companion/Lidarr.AIImport.Companion/Lidarr.AIImport.Companion.csproj --configuration Release --no-restore --output /app -p:NuGetAudit=false -p:EnableAnalyzers=false

FROM mcr.microsoft.com/dotnet/aspnet@sha256:63a1e42746a20795273c219f1577a8826644f12f67d829e818d8ccec92915493
LABEL org.opencontainers.image.source="https://github.com/mshihadeh1/Lidarr.Plugin.AIImport" \
      org.opencontainers.image.licenses="GPL-3.0-only"
WORKDIR /app
COPY --from=build /app ./
COPY LICENSE /licenses/LICENSE
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Lidarr.AIImport.Companion.dll"]
