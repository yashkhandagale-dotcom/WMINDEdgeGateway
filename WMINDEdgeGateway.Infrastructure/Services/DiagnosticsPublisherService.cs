using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using WMINDEdgeGateway.Infrastructure.Diagnostics;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    /// <summary>
    /// Publishes gateway diagnostics to RabbitMQ every 30 seconds.
    /// Queue: wmind_diagnostics (single shared queue — all gateways publish here)
    /// gatewayId is included in the payload so cloud can identify the source.
    /// </summary>
    public sealed class DiagnosticsPublisherService : BackgroundService
    {
        private readonly ILogger<DiagnosticsPublisherService> _log;
        private readonly IModel _channel;
        private readonly string _gatewayId;

        // ── Single shared queue for ALL gateways ──────────────────────────────
        private const string QueueName        = "wmind_diagnostics";
        private const int    PublishIntervalMs = 30_000;

        private static readonly JsonSerializerOptions _json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public DiagnosticsPublisherService(
            ILogger<DiagnosticsPublisherService> log,
            IModel channel,
            string gatewayId)
        {
            _log       = log;
            _channel   = channel;
            _gatewayId = gatewayId;

            // Declare queue once — non-durable (diagnostics don't need persistence)
            _channel.QueueDeclare(
                queue:      QueueName,
                durable:    false,
                exclusive:  false,
                autoDelete: false,
                arguments:  null
            );

            _log.LogInformation(
                "DiagnosticsPublisher → queue: {Queue}, gatewayId: {GatewayId}, interval: {Interval}s",
                QueueName, _gatewayId, PublishIntervalMs / 1000);

            Console.WriteLine($"Diagnostics publisher started → {QueueName} every {PublishIntervalMs / 1000}s (gateway: {_gatewayId})");
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            // Publish once immediately on startup so dashboard gets data right away
            TryPublish();

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PublishIntervalMs, ct);
                TryPublish();
            }
        }

        private void TryPublish()
        {
            try   { Publish(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DiagnosticsPublisher failed to publish");
            }
        }

        private void Publish()
        {
            var s       = GatewayDiagnosticsState.Instance;
            var res     = s.Resources;
            var history = s.GetDowntimeHistory();

            var payload = new
            {
                // ── Gateway Identity ──────────────────────────────────────────
                gatewayId = _gatewayId,   // cloud reads this to route to correct store entry

                // ── Gateway Health ────────────────────────────────────────────
                gatewayOnline   = true,
                lastSeenUtc     = s.LastSeenUtc,
                uptimeToday     = UptimeCalculator.UptimePercent(history, 1),
                uptime7Days     = UptimeCalculator.UptimePercent(history, 7),
                uptimeBar24h    = UptimeCalculator.HourlyBar(history),
                incidentCount   = history.Count,
                totalDowntime   = UptimeCalculator.TotalDowntime(history).ToString(@"hh\:mm\:ss"),
                longestDowntime = UptimeCalculator.LongestDowntime(history).ToString(@"hh\:mm\:ss"),
                recentDowntimes = history.Count > 0
                    ? Enumerable.TakeLast(history, 5)
                        .Select(r => new
                        {
                            start    = r.Start,
                            end      = r.End,
                            duration = r.Duration.ToString(@"hh\:mm\:ss"),
                            reason   = r.Reason
                        })
                    : null,

                // ── Connection Health ─────────────────────────────────────────
                rabbitmq = new
                {
                    state          = s.RabbitMqStatus.State,
                    latencyMs      = s.RabbitMqStatus.LatencyMs,
                    lastCheckedUtc = s.RabbitMqStatus.LastCheckedUtc
                },
                influxDb = new
                {
                    state        = s.InfluxDbStatus.State,
                    writeRateRps = s.InfluxDbStatus.WriteRate,
                    lastWriteUtc = s.InfluxDbStatus.LastWriteUtc
                },
                cloudSync = new
                {
                    state          = s.CloudSyncStatus.State,
                    syncLagSeconds = s.CloudSyncStatus.SyncLagSeconds,
                    lastSyncUtc    = s.CloudSyncStatus.LastSyncUtc
                },

                // ── Protocol & Device Status ──────────────────────────────────
                modbusTcp = s.ModbusTcpDevices.Values.Select(d => new
                {
                    device    = d.DeviceName,
                    ip        = d.Address,
                    state     = d.State,
                    lastError = d.LastError
                }),
                modbusRtu = s.ModbusRtuDevices.Values.Select(d => new
                {
                    slave     = d.DeviceName,
                    slaveId   = d.Address,
                    state     = d.State,
                    lastError = d.LastError
                }),
                opcua = s.OpcUaDevices.Values.Select(d => new
                {
                    device    = d.DeviceName,
                    mode      = d.Mode,
                    state     = d.State,
                    lastError = d.LastError
                }),

                // ── Resource Usage ────────────────────────────────────────────
                cpu            = res.CpuPercent,
                ram            = res.RamPercent,
                disk           = res.DiskPercent,
                netInKbps      = res.NetInKbps,
                netOutKbps     = res.NetOutKbps,
                processRunning = res.ProcessAlive
            };

            var body  = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, _json));
            var props = _channel.CreateBasicProperties();
            props.Persistent  = false;
            props.ContentType = "application/json";

            _channel.BasicPublish(
                exchange:        "",
                routingKey:      QueueName,
                basicProperties: props,
                body:            body
            );

            _log.LogDebug(
                "DiagnosticsPublisher: published | gateway={Gateway} cpu={Cpu} ram={Ram} disk={Disk}",
                _gatewayId, res.CpuPercent, res.RamPercent, res.DiskPercent);
        }
    }
}