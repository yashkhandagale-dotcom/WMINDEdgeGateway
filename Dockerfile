# ─────────────────────────────────────────────────────────────────────────────
# Stage 1 – Build
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy ALL .csproj files (preserving folder structure for restore)
COPY ["WMINDEdgeGateway/WMINDEdgeGateway.csproj",                         "WMINDEdgeGateway/"]
COPY ["WMINDEdgeGateway.Application/WMINDEdgeGateway.Application.csproj", "WMINDEdgeGateway.Application/"]
COPY ["WMINDEdgeGateway.Domain/WMINDEdgeGateway.Domain.csproj",           "WMINDEdgeGateway.Domain/"]
COPY ["WMINDEdgeGateway.Infrastructure/WMINDEdgeGateway.Infrastructure.csproj", "WMINDEdgeGateway.Infrastructure/"]

# Restore using the startup project (it references all others)
RUN dotnet restore "WMINDEdgeGateway/WMINDEdgeGateway.csproj"
# Copy the full source
COPY . .

# Publish a self-contained, trimmed Release build
RUN dotnet publish "WMINDEdgeGateway/WMINDEdgeGateway.csproj" \
        -c Release \
        -o /app/publish \
        --no-restore

# ─────────────────────────────────────────────────────────────────────────────
# Stage 2 – Runtime
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Non-root user for security
RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser

# Copy published output
COPY --from=build /app/publish .

# Ensure the app user owns the files
RUN chown -R appuser:appgroup /app
USER appuser

# ── Environment variable defaults (overridden via docker-compose / -e flags) ──

# Logging
ENV Logging__LogLevel__Default="Information"
ENV Logging__LogLevel__Microsoft="Warning"
ENV Logging__LogLevel__Microsoft__Hosting__Lifetime="Information"

# Modbus
ENV Modbus__MaxConcurrentPolls="10"
ENV Modbus__FailureThreshold="3"

# Modbus RTU
ENV ModbusRtu__FailureThreshold="2"
ENV ModbusRtu__InterFrameGapMs="5"
ENV ModbusRtu__ResponseTimeoutMs="3000"
ENV ModbusRtu__DataBits="8"
ENV ModbusRtu__StopBits="1"

# InfluxDB
ENV InfluxDB__Url="http://influxdb:8086"
ENV InfluxDB__Token=""
ENV InfluxDB__Org="Wonderbiz"
ENV InfluxDB__Bucket="SignalGateway"

# M2M
ENV M2M__ClientId=""
ENV M2M__ClientSecret=""
ENV M2M__TokenUrl=""
ENV M2M__ApiUrl=""
ENV M2M__ConfigUrl=""

# Gateway
ENV Gateway__ClientId=""
ENV Gateway__ClientSecret=""

# Auth & Device API
ENV Auth__BaseUrl=""
ENV DeviceApi__BaseUrl=""

# Cache
ENV Cache__ConfigurationsMinutes="30"

# RabbitMQ
ENV RabbitMq__HostName="rabbitmq"
ENV RabbitMq__Port="5672"
ENV RabbitMq__UserName="guest"
ENV RabbitMq__Password="guest"
ENV RabbitMq__PollIntervalSeconds="5"

ENTRYPOINT ["dotnet", "WMINDEdgeGateway.dll"]
