using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace WMINDEdgeGateway.Infrastructure.Diagnostics
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Shared singleton — all services write here; DiagnosticsApiService reads
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class GatewayDiagnosticsState
    {
        // ── Singleton ────────────────────────────────────────────────────────
        public static readonly GatewayDiagnosticsState Instance = new();
        private GatewayDiagnosticsState() { }

        // ── 1. Gateway Health ─────────────────────────────────────────────────
        public DateTime GatewayStartTime { get; } = DateTime.UtcNow;
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        // Downtime incidents (thread-safe list)
        private readonly List<DowntimeRecord> _downtimeHistory = new();
        private readonly object _dtLock = new();
        private DateTime? _currentDowntimeStart;

        public void RecordDowntimeStart(string reason)
        {
            lock (_dtLock)
            {
                if (_currentDowntimeStart == null)
                    _currentDowntimeStart = DateTime.UtcNow;
            }
        }

        public void RecordDowntimeEnd()
        {
            lock (_dtLock)
            {
                if (_currentDowntimeStart.HasValue)
                {
                    var end = DateTime.UtcNow;
                    _downtimeHistory.Add(new DowntimeRecord
                    {
                        Start = _currentDowntimeStart.Value,
                        End = end,
                        Duration = end - _currentDowntimeStart.Value,
                        Reason = "Recovered"
                    });
                    _currentDowntimeStart = null;
                }
            }
        }

        public IReadOnlyList<DowntimeRecord> GetDowntimeHistory()
        {
            lock (_dtLock) { return _downtimeHistory.AsReadOnly(); }
        }

        // ── 2. Connection Health ───────────────────────────────────────────────
        public ConnectionStatus RabbitMqStatus { get; set; } = new();
        public ConnectionStatus InfluxDbStatus { get; set; } = new();
        public ConnectionStatus CloudSyncStatus { get; set; } = new();

        // ── 3. Protocol / Device Status ────────────────────────────────────────
        public ConcurrentDictionary<string, DeviceStatus> ModbusTcpDevices = new();
        public ConcurrentDictionary<string, DeviceStatus> ModbusRtuDevices = new();
        public ConcurrentDictionary<string, DeviceStatus> OpcUaDevices = new();

        // ── 5. Resource Usage (refreshed by ResourceMonitor) ──────────────────
        public ResourceSnapshot Resources { get; set; } = new();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Data transfer objects stored in the state
    // ─────────────────────────────────────────────────────────────────────────
    public class DowntimeRecord
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public TimeSpan Duration { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public class ConnectionStatus
    {
        public string State { get; set; } = "unknown"; // connected / disconnected / retrying / writing / syncing / error
        public double LatencyMs { get; set; }
        public double WriteRate { get; set; }  // rec/s — InfluxDB
        public double SyncLagSeconds { get; set; }  // Cloud sync
        public DateTime? LastCheckedUtc { get; set; }
        public DateTime? LastWriteUtc { get; set; }
        public DateTime? LastSyncUtc { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class DeviceStatus
    {
        public string DeviceName { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;  // IP or SlaveId
        public string State { get; set; } = "unknown";     // connected / timeout / no_response / crc_error / broken
        public string Mode { get; set; } = string.Empty;  // polling / subscribed (OPC UA)
        public string? LastError { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }

    public class ResourceSnapshot
    {
        public double CpuPercent { get; set; }
        public double RamPercent { get; set; }
        public double DiskPercent { get; set; }
        public double NetInKbps { get; set; }
        public double NetOutKbps { get; set; }
        public bool ProcessAlive { get; set; } = true;
        public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    }
}