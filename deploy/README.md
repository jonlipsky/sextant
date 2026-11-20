# Sextant container

The image includes the .NET SDK because Sextant evaluates repositories with MSBuild at runtime. The SDK tag is
pinned by default and can be overridden with `DOTNET_SDK_VERSION` when building. The build context is the repository
root:

```sh
docker build --build-arg DOTNET_SDK_VERSION=10.0.403 -f deploy/Dockerfile -t sextant:local .
```

For a multi-architecture registry image, use Buildx:

```sh
docker buildx build --platform linux/amd64,linux/arm64 \
  --build-arg DOTNET_SDK_VERSION=10.0.403 \
  -f deploy/Dockerfile -t registry.example/sextant:latest --push .
```

Run with a persistent data volume and Docker secrets or equivalent mounted secret files. The service listens on port
3011; provide the required control token through the supported `_FILE` setting:

```sh
docker run -d --name sextant \
  --restart unless-stopped \
  --cpus=10 --memory=7g \
  -p 3011:3011 \
  -v sextant-data:/data \
  -v /path/to/control-token:/run/secrets/control-token:ro \
  -e SEXTANT_SERVICE_DB_PATH=/data/catalog.db \
  -e SEXTANT_SERVICE_DATA_ROOT=/data \
  -e SEXTANT_SERVICE_BIND_ADDRESS=0.0.0.0 \
  -e SEXTANT_SERVICE_CONTROL_TOKEN_FILE=/run/secrets/control-token \
  sextant:local
```

The service's default volume layout keeps the catalog, checkouts, artifacts, and caches under the configured data root.
For any credential below, mount a read-only secret file and set the named environment variable with the `_FILE` suffix:

| Secret file setting | Credential |
| --- | --- |
| `SEXTANT_SERVICE_CHECKOUT_TOKEN_FILE` | GitHub checkout token |
| `SEXTANT_SERVICE_CONTROL_TOKEN_FILE` | Control-plane bearer token |
| `SEXTANT_SERVICE_QUERY_TOKEN_FILE` | Query-plane bearer token |
| `SEXTANT_SERVICE_CONTRIBUTE_TOKEN_FILE` | Contribution endpoint bearer token |
| `SEXTANT_SERVICE_DELEGATE_TOKENS_FILE` | Semicolon-separated delegate bearer tokens |
| `SEXTANT_SERVICE_CALLER_KEYS_FILE` | Caller assertion signing keys |
| `SEXTANT_SERVICE_READ_POLICY_FILE` | Read-policy tokens and repository grants |
| `SEXTANT_SERVICE_PACKAGE_SOURCE_CREDENTIALS_FILE` | Private package-source credentials |
| `SEXTANT_PEER_QUERY_TOKEN_FILE` | Remote peer query token (local MCP federation) |
| `SEXTANT_CONTRIB_TOKEN_FILE` | `sextant contribute` CLI bearer token |

Never put secret values in Docker build arguments or image layers.
