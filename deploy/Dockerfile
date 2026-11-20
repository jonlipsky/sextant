ARG DOTNET_SDK_VERSION=10.0.403

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS publish
WORKDIR /src
COPY src/ src/
COPY README.md LICENSE ./
RUN dotnet publish src/Sextant.Cli/Sextant.Cli.csproj \
    --configuration Release \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS runtime
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
WORKDIR /app

RUN apt-get update \
    && apt-get install --yes --no-install-recommends git ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*

RUN dotnet workload install macos --skip-manifest-update

COPY deploy/PrefetchPlatformPacks.csproj deploy/PrefetchPlatformPacks.cs /tmp/platform-packs/
RUN dotnet run --project /tmp/platform-packs/PrefetchPlatformPacks.csproj \
        --configuration Release \
        -- /usr/share/dotnet \
    && rm -rf /tmp/platform-packs

COPY deploy/WindowsReferencePacks.csproj /tmp/windows-reference-packs/ReferencePacks.csproj
RUN dotnet restore /tmp/windows-reference-packs/ReferencePacks.csproj \
        --source https://api.nuget.org/v3/index.json \
        --disable-build-servers \
        -nodeReuse:false \
    && set -eu \
    && for pack in microsoft.windowsdesktop.app.ref microsoft.windows.sdk.net.ref; do \
           found=false; \
           for package in "/root/.nuget/packages/$pack/"*; do \
               [ -d "$package" ] || continue; \
               version="$(basename "$package")"; \
               case "$pack" in \
                   microsoft.windowsdesktop.app.ref) destination="Microsoft.WindowsDesktop.App.Ref" ;; \
                   microsoft.windows.sdk.net.ref) destination="Microsoft.Windows.SDK.NET.Ref" ;; \
               esac; \
               mkdir -p "/usr/share/dotnet/packs/$destination/$version"; \
               cp -a "$package/." "/usr/share/dotnet/packs/$destination/$version/"; \
               found=true; \
           done; \
           [ "$found" = true ]; \
       done \
    && rm -rf /root/.nuget/packages \
    && chmod -R a+rX /usr/share/dotnet/packs

COPY --from=publish --chown=$APP_UID:$APP_UID /app/publish/ /app/
RUN mkdir -p /data && chown "$APP_UID:$APP_UID" /data
VOLUME ["/data"]
EXPOSE 3011
USER $APP_UID
ENTRYPOINT ["dotnet", "Sextant.Cli.dll", "service"]
