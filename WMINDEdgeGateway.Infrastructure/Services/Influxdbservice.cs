using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Application.Interfaces;
using WMINDEdgeGateway.Infrastructure.Diagnostics;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class InfluxDbService : IInfluxDbService, IDisposable
    {
        private readonly ILogger<InfluxDbService> _log;
        private readonly InfluxDBClient _client;
        private readonly string _bucket;
        private readonly string _org;

        public InfluxDbService(ILogger<InfluxDbService> log, IConfiguration config)
        {
            _log = log;
            var url = config["InfluxDB:Url"] ?? "http://localhost:8087";
            var token = config["InfluxDB:Token"];
            _bucket = config["InfluxDB:Bucket"] ?? "SignalGateway";
            _org = config["InfluxDB:Org"] ?? "Wonderbiz";

            if (string.IsNullOrEmpty(token))
                _log.LogWarning("InfluxDB token not configured.");

            var options = new InfluxDBClientOptions(url) { Token = token, Org = _org };
            _client = new InfluxDBClient(options);

            _log.LogInformation("InfluxDB client initialized: {Url}, Bucket: {Bucket}, Org: {Org}",
                url, _bucket, _org);
        }

        public async Task WriteAsync(IEnumerable<TelemetryPayload> payloads,
                                     CancellationToken cancellationToken = default)
        {
            if (payloads == null || !payloads.Any())
            {
                _log.LogDebug("No payloads to write to InfluxDB");
                return;
            }

            // ── FIX: materialise list + start stopwatch BEFORE the try ────────
            var payloadList = payloads.ToList();
            var sw = Stopwatch.StartNew();
            // ─────────────────────────────────────────────────────────────────

            try
            {
                var writeApi = _client.GetWriteApiAsync();

                var points = payloadList.Select(p => PointData
                    .Measurement("modbus_telemetry")
                    .Tag("signal_id", p.SignalId)
                    .Field("value", p.Value)
                    .Timestamp(p.Timestamp, WritePrecision.Ms)
                ).ToList();

                await writeApi.WritePointsAsync(points, _bucket, _org, cancellationToken);

                // ── FIX: use payloadList and sw (now declared above) ──────────
                sw.Stop();
                double elapsed = sw.Elapsed.TotalSeconds;
                double writeRate = elapsed > 0 ? payloadList.Count / elapsed : 0;

                var s = GatewayDiagnosticsState.Instance.InfluxDbStatus;
                s.State = "writing";
                s.WriteRate = Math.Round(writeRate, 1);
                s.LastWriteUtc = DateTime.UtcNow;
                s.ErrorMessage = null;
                // ─────────────────────────────────────────────────────────────
            }
            catch (Exception ex)
            {
                var s = GatewayDiagnosticsState.Instance.InfluxDbStatus;
                s.State = "error";
                s.ErrorMessage = ex.Message;

                _log.LogError(ex, "Failed to write {Count} points to InfluxDB", payloadList.Count);
                throw;
            }
        }

        public void Dispose() => _client?.Dispose();
    }
}