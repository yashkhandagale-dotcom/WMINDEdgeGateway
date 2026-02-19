using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Infrastructure.Caching;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    /// <summary>
    /// Unified telemetry payload structure - SignalId, Value, Timestamp
    /// This will be pushed to InfluxDB
    /// </summary>
    public record TelemetryPayload(
        string SignalId,
        object Value,
        DateTime Timestamp
    );

    public class ModbusPollerHostedService : BackgroundService
    {
        private readonly ILogger<ModbusPollerHostedService> _log;
        private readonly IConfiguration _config;
        private readonly MemoryCacheService _cache;
        private readonly IInfluxDbService _influxDb; // Inject InfluxDB service

        private readonly SemaphoreSlim _semaphore;
        private static readonly object _consoleLock = new();
        private readonly ConcurrentDictionary<Guid, Task> _deviceTasks = new();

        private readonly int _failThreshold;

        public ModbusPollerHostedService(
            ILogger<ModbusPollerHostedService> log,
            IConfiguration config,
            MemoryCacheService cache,
            IInfluxDbService influxDb)
        {
            _log = log;
            _config = config;
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _influxDb = influxDb ?? throw new ArgumentNullException(nameof(influxDb));

            _failThreshold = config?.GetValue<int?>("Modbus:FailureThreshold") ?? 3;
            if (_failThreshold <= 0) _failThreshold = 3;

            int concurrency = config?.GetValue<int?>("Modbus:MaxConcurrentPolls") ?? 10;
            if (concurrency <= 0) concurrency = 10;
            _semaphore = new SemaphoreSlim(concurrency, concurrency);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("Modbus poller started with InfluxDB integration.");

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var deviceConfigs = _cache.Get<List<DeviceConfigurationDto>>("ModbusDevices");

                        if (deviceConfigs == null || !deviceConfigs.Any())
                        {
                            _log.LogWarning("No device configurations in cache.");
                            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                            continue;
                        }

                        foreach (var config in deviceConfigs)
                        {
                            // Only handle Modbus (Protocol == 1)
                            if (config.Protocol != 1) continue;

                            if (_deviceTasks.ContainsKey(config.Id)) continue;

                            var task = Task.Run(
                                () => PollLoopForDeviceAsync(config, stoppingToken),
                                stoppingToken);

                            _deviceTasks.TryAdd(config.Id, task);

                            // Clean up completed tasks
                            foreach (var k in _deviceTasks
                                .Where(kvp => kvp.Value.IsCompleted)
                                .Select(kvp => kvp.Key)
                                .ToList())
                                _deviceTasks.TryRemove(k, out _);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Poll loop manager error.");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
            finally
            {
                try { await Task.WhenAll(_deviceTasks.Values.ToArray()); }
                catch { /* ignore during shutdown */ }
            }
        }

        private async Task PollLoopForDeviceAsync(DeviceConfigurationDto deviceConfig, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int delayMs = 1000;
                    try
                    {
                        delayMs = await PollSingleDeviceOnceAsync(deviceConfig, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Poll error for device {Device}", deviceConfig.Id);
                        delayMs = 1000;
                    }

                    if (delayMs <= 0) delayMs = 1000;
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Loop error for device {Device}", deviceConfig.Id);
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
            }
        }

        private async Task<int> PollSingleDeviceOnceAsync(DeviceConfigurationDto deviceConfig, CancellationToken ct)
        {
            int defaultInterval = (deviceConfig.PollIntervalMs ?? 1000) > 0
                ? deviceConfig.PollIntervalMs!.Value : 1000;

            if (deviceConfig.Slaves == null || !deviceConfig.Slaves.Any())
            {
                _log.LogWarning("Device {DeviceId} has no slaves - skipping.", deviceConfig.Id);
                return defaultInterval;
            }

            // Read connection details directly from DTO
            var ip = deviceConfig.IpAddress;
            var port = deviceConfig.Port ?? 502;
            var endian = deviceConfig.Endian ?? "Big";

            if (string.IsNullOrEmpty(ip))
            {
                _log.LogWarning("Device {DeviceId} has no IpAddress. Skipping.", deviceConfig.Id);
                return defaultInterval;
            }

            // Flatten all registers across all slaves
            var activeRegisters = deviceConfig.Slaves
                .SelectMany(ds => ds.Registers
                    .Select(r => new { DeviceSlave = ds, Register = r }))
                .ToList();

            if (!activeRegisters.Any())
            {
                _log.LogWarning("No registers for device {Device}.", deviceConfig.Id);
                return defaultInterval;
            }

            const int ModbusMaxRegistersPerRead = 125;

            // Detect address style: 40001-based or zero-based
            bool dbUses40001 = deviceConfig.Slaves.Any(s =>
                s.Registers.Any(r => r.RegisterAddress >= 40001));

            int ToProto(int dbAddr)
            {
                if (dbUses40001) return dbAddr - 40001;
                if (dbAddr > 0 && dbAddr < 40001) return dbAddr - 1;
                return dbAddr;
            }

            var protoPorts = activeRegisters
                .Select(x => new
                {
                    x.DeviceSlave,
                    x.Register,
                    ProtoAddr = ToProto(x.Register.RegisterAddress),
                    Length = Math.Max(1, x.Register.RegisterLength)
                })
                .OrderBy(x => x.ProtoAddr)
                .ToList();

            CancellationTokenSource? connectCts = null;
            await _semaphore.WaitAsync(ct);
            try
            {
                using var tcp = new TcpClient();
                connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connectCts.Token);

                await tcp.ConnectAsync(ip, port, linked.Token);

                // Single timestamp for the entire poll batch
                var now = DateTime.UtcNow;
                var payloads = new List<TelemetryPayload>();

                // Group by SlaveIndex so we never mix slaves in one request
                var protoGroups = protoPorts
                    .GroupBy(x => x.DeviceSlave.SlaveIndex)
                    .ToDictionary(g => g.Key, g => g.OrderBy(p => p.ProtoAddr).ToList());

                foreach (var kv in protoGroups)
                {
                    int unitId = kv.Key;
                    var itemsForSlave = kv.Value;

                    // Build contiguous ranges up to ModbusMaxRegistersPerRead
                    var slaveRanges = new List<(int Start, int Count, List<dynamic> Items)>();
                    int j = 0;
                    while (j < itemsForSlave.Count)
                    {
                        int start = itemsForSlave[j].ProtoAddr;
                        int end = start + itemsForSlave[j].Length - 1;
                        var items = new List<dynamic> { itemsForSlave[j] };
                        j++;

                        while (j < itemsForSlave.Count)
                        {
                            var next = itemsForSlave[j];
                            if (next.ProtoAddr <= end + 1)
                            {
                                end = Math.Max(end, next.ProtoAddr + next.Length - 1);
                                items.Add(next);
                                j++;
                            }
                            else break;

                            if (end - start + 1 >= ModbusMaxRegistersPerRead)
                            {
                                end = start + ModbusMaxRegistersPerRead - 1;
                                break;
                            }
                        }

                        slaveRanges.Add((start, Math.Min(ModbusMaxRegistersPerRead, end - start + 1), items));
                    }

                    foreach (var range in slaveRanges)
                    {
                        if (range.Start < 0 || range.Start > ushort.MaxValue || range.Count <= 0) continue;

                        try
                        {
                            ushort[] regs = await ModbusTcpClient.ReadHoldingRegistersAsync(
                                tcp, (byte)unitId, (ushort)range.Start, (ushort)range.Count, ct);

                            foreach (var entry in range.Items)
                            {
                                DeviceSlaveDto ds = (DeviceSlaveDto)entry.DeviceSlave;
                                DeviceRegisterDto reg = (DeviceRegisterDto)entry.Register;

                                int relativeIndex = (int)entry.ProtoAddr - range.Start;

                                if (relativeIndex < 0 || relativeIndex + ((int)entry.Length - 1) >= regs.Length)
                                    continue;

                                // *** KEY CHANGE: Skip registers with no SignalId ***
                                if (reg.SignalId == null || reg.SignalId == Guid.Empty)
                                {
                                    _log.LogDebug("Skipping register {RegAddr} - no SignalId mapped", reg.RegisterAddress);
                                    continue;
                                }

                                double finalValue;
                                try
                                {
                                    if (string.Equals(reg.DataType, "float32", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (relativeIndex + 1 >= regs.Length) continue;

                                        ushort r1 = regs[relativeIndex];
                                        ushort r2 = regs[relativeIndex + 1];
                                        byte[] bytes = new byte[4];

                                        if (reg.WordSwap)
                                        {
                                            bytes[0] = (byte)(r2 >> 8); bytes[1] = (byte)(r2 & 0xFF);
                                            bytes[2] = (byte)(r1 >> 8); bytes[3] = (byte)(r1 & 0xFF);
                                        }
                                        else
                                        {
                                            bytes[0] = (byte)(r1 >> 8); bytes[1] = (byte)(r1 & 0xFF);
                                            bytes[2] = (byte)(r2 >> 8); bytes[3] = (byte)(r2 & 0xFF);
                                        }

                                        if (string.Equals(endian, "Little", StringComparison.OrdinalIgnoreCase))
                                            Array.Reverse(bytes);

                                        float raw = BitConverter.ToSingle(bytes, 0);

                                        if (float.IsNaN(raw) || float.IsInfinity(raw) || Math.Abs(raw) > 1e6)
                                            raw = 0;

                                        finalValue = ((r1 == 0 && r2 == 0) || Math.Abs(raw) < 1e-3)
                                            ? r1 * reg.Scale
                                            : raw * reg.Scale;
                                    }
                                    else
                                    {
                                        finalValue = regs[relativeIndex] * reg.Scale;
                                    }

                                    // *** UNIFIED PAYLOAD: Map register reading to SignalId ***
                                    payloads.Add(new TelemetryPayload(
                                        SignalId: reg.SignalId.Value.ToString(),
                                        Value: finalValue,
                                        Timestamp: now
                                    ));
                                }
                                catch (Exception decodeEx)
                                {
                                    _log.LogWarning(decodeEx, "Decode failed for register {RegId}", reg.RegisterId);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "Read error device {Device} unit={UnitId}", deviceConfig.Id, unitId);
                        }
                    }
                }

                // *** PUSH TO INFLUXDB ***
                if (payloads.Any())
                {
                    try
                    {
                        await _influxDb.WriteAsync(payloads, ct);

                        _log.LogInformation("Pushed {Count} telemetry points to InfluxDB for device {Device}",
                            payloads.Count, deviceConfig.DeviceName);

                        // Optional: Print to console for debugging
                        lock (_consoleLock)
                        {
                            Console.WriteLine();
                            Console.WriteLine(new string('=', 65));
                            Console.WriteLine($"Device    : {deviceConfig.DeviceName} | {ip}:{port}");
                            Console.WriteLine($"Timestamp : {now:yyyy-MM-dd HH:mm:ss} UTC");
                            Console.WriteLine($"Payloads  : {payloads.Count} ? InfluxDB");
                            Console.WriteLine(new string('-', 65));
                            Console.WriteLine($"  {"SignalId",-38} {"Value",10}");
                            Console.WriteLine(new string('-', 65));

                            foreach (var p in payloads.Take(10)) // Show first 10
                                Console.WriteLine($"  {p.SignalId,-38} {p.Value,10:G6}");

                            if (payloads.Count > 10)
                                Console.WriteLine($"  ... and {payloads.Count - 10} more");

                            Console.WriteLine(new string('=', 65));
                        }
                    }
                    catch (Exception influxEx)
                    {
                        _log.LogError(influxEx, "Failed to write {Count} points to InfluxDB for device {Device}",
                            payloads.Count, deviceConfig.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (connectCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
            {
                // Connection timeout (not application shutdown)
                _log.LogWarning("Connection timeout for device {Device} at {Ip}:{Port}",
                    deviceConfig.Id, ip, port);
            }
            catch (SocketException s_ex)
            {
                _log.LogWarning(s_ex, "Device {Device} unreachable at {Ip}:{Port}", deviceConfig.Id, ip, port);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _log.LogDebug("Polling cancelled for device {Device}", deviceConfig.Id);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error polling device {Device}", deviceConfig.Id);
            }
            finally
            {
                connectCts?.Dispose();
                _semaphore.Release();
            }

            return defaultInterval;
        }
    }
}