using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO.Ports;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Infrastructure.Caching;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class ModbusRtuPollerHostedService : BackgroundService
    {
        private readonly ILogger<ModbusRtuPollerHostedService> _log;
        private readonly IConfiguration _config;
        private readonly MemoryCacheService _cache;
        private readonly IInfluxDbService _influxDb;

        private static readonly object _consoleLock = new();

        public ModbusRtuPollerHostedService(
            ILogger<ModbusRtuPollerHostedService> log,
            IConfiguration config,
            MemoryCacheService cache,
            IInfluxDbService influxDb)
        {
            _log = log;
            _config = config;
            _cache = cache;
            _influxDb = influxDb;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("Modbus RTU Poller started.");

            var deviceConfigs = _cache.Get<List<DeviceConfigurationDto>>("ModbusRtuDevices");
            if (deviceConfigs == null || !deviceConfigs.Any())
            {
                _log.LogWarning("No Modbus RTU devices found.");
                return;
            }

            var byPort = deviceConfigs
                .Where(d => !string.IsNullOrWhiteSpace(d.SerialPort))
                .GroupBy(d => d.SerialPort!)
                .ToDictionary(g => g.Key, g => g.ToList());

            var tasks = byPort.Select(kv =>
                Task.Run(() => PollPortAsync(kv.Key, kv.Value, stoppingToken), stoppingToken));

            await Task.WhenAll(tasks);
        }

        private async Task PollPortAsync(
            string comPort,
            List<DeviceConfigurationDto> devices,
            CancellationToken ct)
        {
            var first = devices.First();

            using var port = new SerialPort(comPort)
            {
                BaudRate = first.BaudRate ?? 9600,
                DataBits = _config.GetValue<int>("ModbusRtu:DataBits", 8),
                Parity = ParseParity(first.Parity),
                StopBits = ParseStopBits(_config.GetValue<string>("ModbusRtu:StopBits", "1")),
                ReadTimeout = 3000,
                WriteTimeout = 3000
            };

            try
            {
                port.Open();
                _log.LogInformation("Serial port {Port} opened at {Baud} baud.", comPort, port.BaudRate);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Cannot open serial port {Port}", comPort);
                return;
            }

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    foreach (var device in devices)
                    {
                        try
                        {
                            await PollDeviceAsync(device, port, ct);
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "RTU poll error for {Device}", device.DeviceName);
                        }

                        await Task.Delay(50, ct);   // RS-485 silent interval
                    }

                    int interval = devices.Min(d => d.PollIntervalMs ?? 1000);
                    await Task.Delay(interval, ct);
                }
            }
            finally
            {
                try { port.Close(); } catch { }
            }
        }

        private async Task PollDeviceAsync(
            DeviceConfigurationDto device,
            SerialPort port,
            CancellationToken ct)
        {
            if (device.Slaves == null || !device.Slaves.Any()) return;

            var payloads = new List<TelemetryPayload>();
            var now = DateTime.UtcNow;

            bool dbUses40001 = device.Slaves.Any(s =>
                s.Registers.Any(r => r.RegisterAddress >= 40001));

            int ToProto(int addr) =>
                dbUses40001 ? addr - 40001 :
                (addr > 0 && addr < 40001) ? addr - 1 : addr;

            foreach (var slave in device.Slaves)
            {
                var activeRegs = slave.Registers
                    .Where(r => r.SignalId.HasValue && r.SignalId != Guid.Empty)
                    .OrderBy(r => r.RegisterAddress)
                    .ToList();

                if (!activeRegs.Any()) continue;

                const int maxRegsPerRead = 20;
                int j = 0;

                while (j < activeRegs.Count)
                {
                    int startProto = ToProto(activeRegs[j].RegisterAddress);
                    int endProto = startProto;
                    int batchEnd = j;

                    while (batchEnd < activeRegs.Count - 1)
                    {
                        int nextProto = ToProto(activeRegs[batchEnd + 1].RegisterAddress);
                        if (nextProto - startProto >= maxRegsPerRead) break;
                        endProto = nextProto;
                        batchEnd++;
                    }

                    ushort start = (ushort)startProto;
                    ushort count = (ushort)(endProto - startProto + 1);

                    ushort[] regs = await SafeReadAsync(
                        port, (byte)slave.SlaveIndex, start, count, ct);

                    if (regs == null)
                    {
                        j = batchEnd + 1;
                        continue;
                    }

                    for (int k = j; k <= batchEnd; k++)
                    {
                        var reg = activeRegs[k];
                        int idx = ToProto(reg.RegisterAddress) - startProto;
                        if (idx < 0 || idx >= regs.Length) continue;

                        double val = regs[idx] * reg.Scale;
                        payloads.Add(new TelemetryPayload(
                            reg.SignalId!.Value.ToString(), val, now));
                    }

                    j = batchEnd + 1;
                    await Task.Delay(30, ct);  // frame gap
                }
            }

            if (!payloads.Any()) return;

            await _influxDb.WriteAsync(payloads, ct);

            _log.LogInformation("RTU: {Count} points → InfluxDB ({Device})",
                payloads.Count, device.DeviceName);
        }

        private async Task<ushort[]?> SafeReadAsync(
            SerialPort port,
            byte slave,
            ushort start,
            ushort count,
            CancellationToken ct)
        {
            for (int i = 1; i <= 3; i++)
            {
                try
                {
                    return await ModbusRtuClient.ReadHoldingRegistersAsync(
                        port, slave, start, count, ct);
                }
                catch (TimeoutException) when (i < 3)
                {
                    await Task.Delay(200, ct);
                }
            }

            _log.LogWarning("Timeout: slave {Slave}", slave);
            return null;
        }

        private static Parity ParseParity(string? p) => p?.ToUpperInvariant() switch
        {
            "EVEN" => Parity.Even,
            "ODD" => Parity.Odd,
            "MARK" => Parity.Mark,
            _ => Parity.None
        };

        private static StopBits ParseStopBits(string? s) => s switch
        {
            "2" => StopBits.Two,
            "1.5" => StopBits.OnePointFive,
            _ => StopBits.One
        };
    }
}