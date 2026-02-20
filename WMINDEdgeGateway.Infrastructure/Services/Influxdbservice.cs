using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    /// <summary>
    /// InfluxDB service implementation for writing telemetry data
    /// Uses InfluxDB.Client library (InfluxDB 2.x compatible)
    /// </summary>
    public class InfluxDbService : IInfluxDbService, IDisposable
    {
        private readonly ILogger<InfluxDbService> _log;
        private readonly InfluxDBClient _client;
        private readonly string _bucket;
        private readonly string _org;

        public InfluxDbService(ILogger<InfluxDbService> log, IConfiguration config)
        {
            _log = log;

            // Read InfluxDB configuration
            var url = config["InfluxDB:Url"] ?? "http://localhost:8087";
            var token = config["InfluxDB:Token"];
            _bucket = config["InfluxDB:Bucket"] ?? "SignalGateway";
            _org = config["InfluxDB:Org"] ?? "Wonderbiz";

            if (string.IsNullOrEmpty(token))
            {
                _log.LogWarning("InfluxDB token not configured. Service may fail to authenticate.");
            }

            // Initialize InfluxDB client with correct API
            var options = new InfluxDBClientOptions(url)
            {
                Token = token,
                Org = _org
            };

            _client = new InfluxDBClient(options);

            _log.LogInformation("InfluxDB client initialized: {Url}, Bucket: {Bucket}, Org: {Org}",
                url, _bucket, _org);
        }

        public async Task WriteAsync(IEnumerable<TelemetryPayload> payloads, CancellationToken cancellationToken = default)
        {
            if (payloads == null || !payloads.Any())
            {
                _log.LogDebug("No payloads to write to InfluxDB");
                return;
            }

            try
            {
                // GetWriteApiAsync returns the async write API (not IDisposable)
                var writeApi = _client.GetWriteApiAsync();

                // Convert TelemetryPayload to InfluxDB PointData
                var points = payloads.Select(p => PointData
                    .Measurement("modbus_telemetry")
                    .Tag("signal_id", p.SignalId)
                    .Field("value", p.Value)
                    .Timestamp(p.Timestamp, WritePrecision.Ms)
                ).ToList();

                // Write batch to InfluxDB
                await writeApi.WritePointsAsync(points, _bucket, _org, cancellationToken);

                _log.LogDebug("Successfully wrote {Count} points to InfluxDB", points.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to write {Count} points to InfluxDB", payloads.Count());
                throw;
            }
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}