using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Infrastructure.Caching;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class ModbusPollerHostedService : BackgroundService
    {
        private readonly ILogger<ModbusPollerHostedService> _log;
        private readonly MemoryCacheService _cache;

        private readonly SemaphoreSlim _semaphore = new(10);
        private readonly ConcurrentDictionary<Guid, Task> _deviceTasks = new();
        private static readonly object _consoleLock = new();

        public ModbusPollerHostedService(
            ILogger<ModbusPollerHostedService> log,
            MemoryCacheService cache)
        {
            _log = log;
            _cache = cache;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("Modbus Poller started (EDGE)");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var devices = _cache.Get<List<DeviceConfigurationDto>>("DeviceConfigurations");
                    
                    if (devices == null)
                    {
                        await Task.Delay(5000, stoppingToken);
                        continue;
                    }
                    
                    var modbusDevices = devices.Where(d => 
                        d.Protocol != null && 
                        (d.Protocol.Equals("Modbus", StringComparison.OrdinalIgnoreCase) ||
                         d.Protocol.Equals("ModbusTCP", StringComparison.OrdinalIgnoreCase))
                    ).ToList();

                    foreach (var device in modbusDevices)
                    {
                        if (_deviceTasks.ContainsKey(device.Id))
                            continue;

                        var task = Task.Run(() => PollDeviceLoop(device, stoppingToken), stoppingToken);
                        _deviceTasks.TryAdd(device.Id, task);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Error in ExecuteAsync main loop");
                }

                await Task.Delay(5000, stoppingToken);
            }
        }

        private async Task PollDeviceLoop(DeviceConfigurationDto device, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await PollOnce(device, ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, $"Poll error for device {device.DeviceName}");
                }

                int interval = device.PollIntervalMs > 0 ? device.PollIntervalMs : 1000;
                await Task.Delay(interval, ct);
            }
        }

        private async Task PollOnce(DeviceConfigurationDto device, CancellationToken ct)
        {
            string configJson = device.ConfigurationJson ?? "{}";
            JsonDocument? settings = null;
            
            try
            {
                settings = JsonDocument.Parse(configJson);
            }
            catch
            {
                return;
            }

            // Try multiple possible property names for IP address
            string? ip = GetString(settings, "IpAddress") 
                      ?? GetString(settings, "ipAddress") 
                      ?? GetString(settings, "IPAddress")
                      ?? GetString(settings, "ip")
                      ?? GetString(settings, "IP")
                      ?? GetString(settings, "host")
                      ?? GetString(settings, "Host");
            
            int port = GetInt(settings, "Port", 502);
            if (port == 502)
            {
                port = GetInt(settings, "port", 502);
            }
            
            string endian = GetString(settings, "Endian") 
                         ?? GetString(settings, "endian") 
                         ?? "Big";

            // Check for explicit AddressStyle configuration
            string? addressStyleCfg = GetString(settings, "AddressStyle")
                                   ?? GetString(settings, "addressStyle");

            // Fallback to hardcoded values if IP is missing
            if (string.IsNullOrWhiteSpace(ip))
            {
                ip = "127.0.0.1";
                port = 5020;
            }

            if (device.Slaves == null || device.Slaves.Length == 0)
            {
                return;
            }

            var registers = device.Slaves
                .Where(s => s != null && s.IsHealthy && s.Registers != null)
                .SelectMany(s => s.Registers!
                    .Where(r => r != null && r.IsHealthy)
                    .Select(r => new { Slave = s, Register = r }))
                .ToList();

            if (registers.Count == 0)
            {
                return;
            }

            // Auto-detect addressing style
            bool dbUses40001 = false;
            
            if (!string.IsNullOrEmpty(addressStyleCfg))
            {
                dbUses40001 = string.Equals(addressStyleCfg, "40001", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                dbUses40001 = registers.Any(r => r.Register.RegisterAddress >= 40001);
            }

            await _semaphore.WaitAsync(ct);
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(ip, port, ct);

                var groups = registers.GroupBy(x => x.Slave.SlaveIndex);

                foreach (var grp in groups)
                {
                    int unitId = grp.Key;

                    var ordered = grp
                        .OrderBy(x => x.Register.RegisterAddress)
                        .ToList();

                    foreach (var item in ordered)
                    {
                        // Smart address conversion
                        int dbAddr = item.Register.RegisterAddress;
                        ushort protoAddr;

                        if (dbUses40001)
                        {
                            protoAddr = (ushort)(dbAddr - 40001);
                        }
                        else if (dbAddr > 0 && dbAddr < 40001)
                        {
                            protoAddr = (ushort)(dbAddr - 1);
                        }
                        else
                        {
                            protoAddr = (ushort)dbAddr;
                        }

                        ushort len = (ushort)Math.Max(1, item.Register.RegisterLength);

                        ushort[] regs = await ModbusTcpClient.ReadHoldingRegistersAsync(
                            tcp, (byte)unitId, protoAddr, len, ct);

                        double value;
                        if (item.Register.DataType?.Equals("float32", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            if (regs.Length < 2)
                            {
                                continue;
                            }

                            ushort r1 = regs[0];
                            ushort r2 = regs[1];

                            byte[] bytes = new byte[4];
                            
                            // Handle word swap
                            if (item.Register.WordSwap)
                            {
                                bytes[0] = (byte)(r2 >> 8);
                                bytes[1] = (byte)(r2 & 0xFF);
                                bytes[2] = (byte)(r1 >> 8);
                                bytes[3] = (byte)(r1 & 0xFF);
                            }
                            else
                            {
                                bytes[0] = (byte)(r1 >> 8);
                                bytes[1] = (byte)(r1 & 0xFF);
                                bytes[2] = (byte)(r2 >> 8);
                                bytes[3] = (byte)(r2 & 0xFF);
                            }
                            
                            if (endian == "Little") Array.Reverse(bytes);
                            
                            float raw = BitConverter.ToSingle(bytes, 0);
                            
                            // Handle invalid floats - use fallback to integer interpretation
                            if (float.IsNaN(raw) || float.IsInfinity(raw) || Math.Abs(raw) < 1e-6 || Math.Abs(raw) > 1e6)
                            {
                                value = r1 * item.Register.Scale;
                            }
                            else
                            {
                                value = raw * item.Register.Scale;
                            }
                        }
                        else
                        {
                            value = regs[0] * item.Register.Scale;
                        }

                        lock (_consoleLock)
                        {
                            Console.WriteLine($"[{device.DeviceName}] Voltage: {value:F2}V");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, $"Error polling device {device.DeviceName}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private static string? GetString(JsonDocument doc, string prop)
            => doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
               ? v.GetString()
               : null;

        private static int GetInt(JsonDocument doc, string prop, int def)
            => doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
               && v.TryGetInt32(out var x) ? x : def;
    }
}