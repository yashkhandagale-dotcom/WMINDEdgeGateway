# 🐳 WMIND Edge Gateway — Docker Deployment Guide

> Complete guide to containerizing, configuring, and running the Edge Gateway
> alongside the wmind platform stack.

---

## 📁 Project Structure

```
WMINDEdgeGateway/
├── Dockerfile                          ← Two-stage build (SDK → Runtime)
├── docker-compose.yml                  ← Edge Gateway service definition
├── .env                                ← Your secrets (never commit this)
├── .env.example                        ← Template — copy to .env
├── .dockerignore                       ← Excludes bin/, obj/, secrets from build
├── ca.crt                              ← RabbitMQ TLS certificate (from wmind)
├── WMINDEdgeGateway/                   ← Startup project (Program.cs)
├── WMINDEdgeGateway.Application/       ← Application layer
├── WMINDEdgeGateway.Domain/            ← Domain layer
├── WMINDEdgeGateway.Infrastructure/    ← Infrastructure layer (services)
└── WMINDEdgeGateway.sln
```

---

## ⚙️ Prerequisites

| Requirement | Details |
|---|---|
| Docker Desktop | v24+ with Docker Compose v2 |
| wmind stack | Must be running before starting Edge Gateway |
| `ca.crt` | Download from wmind — place in project root |
| `.env` file | Copy `.env.example` → `.env` and fill in values |

---

## 🚀 Quick Start

### Step 1 — Start wmind stack first

```bash
cd path/to/wmind
docker compose up -d

# Wait until all containers are healthy
docker compose ps
```

The Edge Gateway depends on wmind's network and containers:
- `influxdb` — time-series data storage
- `rabbitmq` — message broker (TLS on port 5671)
- `gateway` — auth server and device config API

---

### Step 2 — Set up your `.env`

```bash
cd WMINDEdgeGateway
cp .env.example .env
```

Fill in these values (copy from wmind's `.env`):

```env
# Gateway identity — from wmind platform
GATEWAY_CLIENT_ID=GW-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
GATEWAY_CLIENT_SECRET=your_gateway_secret

# Auth & Device API — wmind gateway container
AUTH_BASE_URL=http://gateway:5000/
DEVICE_API_BASE_URL=http://gateway:5000/

# InfluxDB — must match wmind's INFLUX_TOKEN, INFLUX_ORG, INFLUX_BUCKET
INFLUXDB_URL=http://influxdb:8086
INFLUXDB_TOKEN=<copy from wmind .env INFLUX_TOKEN>
INFLUXDB_ORG=Wonderbiz
INFLUXDB_BUCKET=SignalValueTeleMentry

# RabbitMQ — must match wmind's RABBIT_USER and RABBIT_PASS
RABBITMQ_HOST=rabbitmq
RABBITMQ_PORT=5671
RABBITMQ_USER=<copy from wmind .env RABBIT_USER>
RABBITMQ_PASSWORD=<copy from wmind .env RABBIT_PASS>
```

---

### Step 3 — Place `ca.crt` in project root

Download `ca.crt` from wmind and place it next to `Dockerfile`:

```
WMINDEdgeGateway/
├── ca.crt        ← here
├── Dockerfile
└── docker-compose.yml
```

This certificate is mounted into the container at `/app/ca.crt` and used to verify RabbitMQ's TLS connection on port 5671.

---

### Step 4 — Build and start

```bash
docker compose up --build -d
```

---

### Step 5 — Verify it's working

```bash
docker compose logs -f edge-gateway
```

✅ **Healthy output looks like this:**

```
Edge Gateway Starting...
InfluxDB client initialized: http://influxdb:8086
Token acquired.
Fetched 1 configuration(s).
Configurations cached in memory.
   TLS: enabled  CA cert: /app/ca.crt
✅ RabbitMQ Connected!  Queue 'telemetry_queue' ready.
Bridge service started.
Diagnostics publisher started → wmind_diagnostics_GW-xxxxx
DiagnosticsPublisher: published | cpu=12.3 ram=18.7 disk=6.9
```

---

## 🏗️ How It Works

### Architecture

```
wmind stack (wmind_tmind-net)
│
├── gateway:5000          ← Edge Gateway gets JWT token here
│                            and fetches device configurations
├── influxdb:8086         ← Edge Gateway writes polled data here
│                            Bridge service reads from here
└── rabbitmq:5671 (TLS)   ← Bridge forwards data to wmind
                             Diagnostics published here too

Edge Gateway (joins wmind_tmind-net)
│
├── TokenService          → GET /oauth/token      (gateway:5000)
├── DeviceServiceClient   → GET /api/deviceConfigs (gateway:5000)
├── ModbusPollerService   → polls physical Modbus TCP/RTU devices
├── OpcUaPollerService    → polls OPC-UA devices
├── InfluxDbService       → writes telemetry to InfluxDB
├── BridgeService         → reads InfluxDB → publishes to RabbitMQ
├── ResourceMonitorService → monitors CPU/RAM/Disk
└── DiagnosticsPublisher  → publishes health stats to RabbitMQ
```

### Startup Sequence

```
1. Load config (appsettings.json + env vars)
2. Connect to InfluxDB
3. Get JWT token from gateway:5000
4. Fetch device configurations for this Gateway ID
5. Partition devices → Modbus TCP / RTU / OPC-UA Polling / PubSub
6. Start pollers (each on background thread)
7. Start Bridge (InfluxDB → RabbitMQ)  ← MUST be before Diagnostics
8. Start Diagnostics Publisher (reuses Bridge's RabbitMQ channel)
9. Run forever until Ctrl+C or docker compose down
```

---

## 🐳 Dockerfile — Explained

```dockerfile
# Stage 1: Build (uses full SDK ~800MB)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
```
Full .NET SDK needed to compile. Discarded after build — never ends up in the final image.

```dockerfile
# Copy .csproj files FIRST (layer caching trick)
COPY ["WMINDEdgeGateway/WMINDEdgeGateway.csproj", "WMINDEdgeGateway/"]
COPY ["WMINDEdgeGateway.Application/...csproj",    "WMINDEdgeGateway.Application/"]
COPY ["WMINDEdgeGateway.Domain/...csproj",         "WMINDEdgeGateway.Domain/"]
COPY ["WMINDEdgeGateway.Infrastructure/...csproj", "WMINDEdgeGateway.Infrastructure/"]
RUN dotnet restore
```
Copying only `.csproj` files first means NuGet packages are cached as a Docker layer. They only re-download when dependencies change, not when you edit `.cs` files. Saves minutes on every rebuild.

```dockerfile
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore
```
Copies all source code then compiles in Release mode into `/app/publish`.

```dockerfile
# Stage 2: Runtime (lean ~200MB — no SDK)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
```
Only the .NET runtime — 4× smaller than the SDK image. The SDK, source code, and build tools are never in the final image.

```dockerfile
RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser
```
Runs as non-root user `appuser`. If the container is ever compromised, the attacker has no root access to the host.

```dockerfile
COPY --from=build /app/publish .
```
Takes only the compiled output from Stage 1 — nothing else crosses over.

```dockerfile
ENV InfluxDB__Url="http://influxdb:8086"
ENV RabbitMq__Port="5672"
```
Default values baked into the image. The double underscore `__` is the .NET convention for nested config — `InfluxDB__Url` maps to `appsettings.json` → `InfluxDB.Url`. All overridden by docker-compose `environment:` block or `.env`.

---

## 🔧 docker-compose.yml — Explained

```yaml
env_file:
  - .env
environment:
  InfluxDB__Token: "${INFLUXDB_TOKEN}"
  RabbitMq__Port: "${RABBITMQ_PORT:-5671}"
```
Two layers of config injection. `env_file` loads raw variables from `.env`. `environment` maps them to .NET config keys using `__` notation. `:-5671` is the fallback value if the variable is missing from `.env`.

```yaml
volumes:
  - ./ca.crt:/app/ca.crt:ro
```
Mounts the TLS certificate from the host into the container at `/app/ca.crt`. `:ro` means read-only — the container can read it but never modify it. The RabbitMQ bridge uses this to validate the server's SSL certificate.

```yaml
networks:
  tmind-net:
    external: true
    name: wmind_tmind-net
```
Joins wmind's **existing** Docker network instead of creating a new one. This is what allows the Edge Gateway container to reach `influxdb`, `rabbitmq`, and `gateway` by container name — they are all on the same virtual network `wmind_tmind-net`.

---

## ⚙️ Environment Variables Reference

### Required (app will fail without these)

| Variable | Maps to | Description |
|---|---|---|
| `GATEWAY_CLIENT_ID` | `Gateway:ClientId` | Gateway identity issued by wmind platform |
| `GATEWAY_CLIENT_SECRET` | `Gateway:ClientSecret` | Gateway secret |
| `AUTH_BASE_URL` | `Auth:BaseUrl` | Must be `http://gateway:5000/` |
| `DEVICE_API_BASE_URL` | `DeviceApi:BaseUrl` | Must be `http://gateway:5000/` |
| `INFLUXDB_TOKEN` | `InfluxDB:Token` | Must match wmind's `INFLUX_TOKEN` |
| `RABBITMQ_USER` | `RabbitMq:UserName` | Must match wmind's `RABBIT_USER` |
| `RABBITMQ_PASSWORD` | `RabbitMq:Password` | Must match wmind's `RABBIT_PASS` |

### Optional (safe defaults provided)

| Variable | Default | Description |
|---|---|---|
| `INFLUXDB_URL` | `http://influxdb:8086` | InfluxDB container URL |
| `INFLUXDB_ORG` | `Wonderbiz` | InfluxDB organisation |
| `INFLUXDB_BUCKET` | `SignalValueTeleMentry` | InfluxDB bucket name |
| `RABBITMQ_HOST` | `rabbitmq` | RabbitMQ container name |
| `RABBITMQ_PORT` | `5671` | TLS port (use 5672 for plain AMQP) |
| `MODBUS_MAX_CONCURRENT_POLLS` | `10` | Max parallel Modbus polls |
| `MODBUS_FAILURE_THRESHOLD` | `3` | Failures before marking device unhealthy |
| `MODBUS_RTU_RESPONSE_TIMEOUT_MS` | `3000` | RTU response timeout (ms) |
| `CACHE_CONFIGURATIONS_MINUTES` | `30` | Device config cache TTL |
| `RABBITMQ_POLL_INTERVAL_SECONDS` | `5` | Bridge poll interval |
| `LOGGING_LEVEL_DEFAULT` | `Information` | Root log level |

---

## 🔄 Common Operations

```bash
# Start (wmind must be running first)
docker compose up --build -d

# Watch logs live
docker compose logs -f edge-gateway

# Stop Edge Gateway only (wmind keeps running)
docker compose down

# Rebuild after code changes
docker compose up --build -d edge-gateway

# Restart without rebuild (after .env changes only)
docker compose down && docker compose up -d

# Check container status
docker compose ps

# Open shell inside container (for debugging)
docker exec -it edge-gateway bash

# Test connectivity to wmind gateway from inside container
docker exec -it edge-gateway curl http://gateway:5000/health

# Verify container joined the correct network
docker inspect edge-gateway --format "{{json .NetworkSettings.Networks}}"
```

---

## 🛑 Stopping Order

Always stop Edge Gateway **before** wmind:

```bash
# 1. Stop Edge Gateway first
cd WMINDEdgeGateway
docker compose down

# 2. Then stop wmind
cd path/to/wmind
docker compose down
```

---

## 🔍 Troubleshooting

| Error | Cause | Fix |
|---|---|---|
| `Connection refused (gateway:5000)` | wmind not started yet | Start wmind first, then Edge Gateway |
| `401 Unauthorized` on token fetch | Wrong `GATEWAY_CLIENT_ID` or `GATEWAY_CLIENT_SECRET` | Check `.env` values match what wmind issued |
| `500 Internal Server Error` on config fetch | Gateway ID not registered in wmind DB | Register the gateway in wmind frontend |
| `network wmind_tmind-net not found` | wmind compose not running | Run `docker compose up -d` in wmind folder first |
| `None of the specified endpoints were reachable` | RabbitMQ TLS failing | Check `ca.crt` exists in project root and port is `5671` |
| `Device unreachable at 127.0.0.1:502` | Wrong IP saved in device config | Fix device IP in wmind frontend — `127.0.0.1` is localhost inside the container |
| `Fetched 0 configuration(s)` | No devices assigned to this gateway | Add devices in wmind frontend under this Gateway ID |

---

## 🔐 Security Notes

- `.env` is git-ignored — **never commit it**
- `ca.crt` is mounted read-only (`:ro`) — container cannot modify it
- App runs as non-root `appuser` inside the container
- No ports are exposed from the Edge Gateway — it only makes outbound connections
- RabbitMQ uses TLS 1.2 with CA certificate validation via `/app/ca.crt`

---

## 📋 Key Files Summary

| File | Purpose |
|---|---|
| `Dockerfile` | Two-stage build: compile with SDK, run with lean runtime |
| `docker-compose.yml` | Service definition, env mapping, network join, cert volume |
| `.env` | Actual secrets and config values (never committed) |
| `.env.example` | Template with all variables documented |
| `.dockerignore` | Prevents `bin/`, `obj/`, `.env`, test projects from entering the image |
| `ca.crt` | wmind RabbitMQ TLS CA certificate — required for port 5671 |
| `InfluxToRabbitMqBridgeService.cs` | TLS-aware RabbitMQ connection — reads `/app/ca.crt` |
| `Program.cs` | `.AddEnvironmentVariables()` — makes all Docker env vars readable by app |
