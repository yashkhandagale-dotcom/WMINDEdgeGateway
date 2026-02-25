using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NModbus;
using NModbus.IO;
using NModbus.Serial;
using System.IO.Ports;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Infrastructure.Caching;

namespace WMINDEdgeGateway.Infrastructure.Services;

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

        var devices = _cache.Get<List<DeviceConfigurationDto>>("ModbusRtuDevices");
        if (devices == null || !devices.Any())
        {
            _log.LogWarning("No Modbus RTU devices configured.");
            return;
        }

        var byPort = devices
            .Where(d => !string.IsNullOrWhiteSpace(d.SerialPort))
            .GroupBy(d => d.SerialPort!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var tasks = byPort.Select(kv =>
            Task.Run(() => PollPortAsync(kv.Key, kv.Value, stoppingToken), stoppingToken));

        await Task.WhenAll(tasks);
    }

    private async Task PollPortAsync(
        string portName,
        List<DeviceConfigurationDto> devices,
        CancellationToken ct)
    {
        var first = devices.First();

        using var port = new SerialPort(portName)
        {
            BaudRate     = first.BaudRate ?? 9600,
            DataBits     = 8,
            Parity       = Parity.None,
            StopBits     = StopBits.One,
            ReadTimeout  = 3000,
            WriteTimeout = 3000
        };

        try
        {
            port.Open();
            _log.LogInformation("Serial port {Port} opened at {Baud}", portName, port.BaudRate);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open {Port}", portName);
            return;
        }

        var factory = new ModbusFactory();
        using var adapter = new SerialPortAdapter(port);
        using var master  = factory.CreateRtuMaster(adapter);

        master.Transport.Retries                 = 2;
        master.Transport.WaitToRetryMilliseconds = 200;

        while (!ct.IsCancellationRequested)
        {
            foreach (var device in devices)
            {
                try
                {
                    await PollDeviceAsync(device, master, ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "RTU poll error for {Device}", device.DeviceName);
                }

                await Task.Delay(50, ct); // RS485 silent gap
            }

            int delay = devices.Min(d => d.PollIntervalMs ?? 1000);
            await Task.Delay(delay, ct);
        }
    }

    private async Task PollDeviceAsync(
        DeviceConfigurationDto device,
        IModbusSerialMaster master,
        CancellationToken ct)
    {
        if (device.Slaves == null || !device.Slaves.Any())
            return;

        var payloads = new List<TelemetryPayload>();
        var now      = DateTime.UtcNow;

        foreach (var slave in device.Slaves)
        {
            var regs = slave.Registers
                .Where(r => r.SignalId.HasValue && r.SignalId != Guid.Empty)
                .Select(r =>
                {
                    r.RegisterAddress = ConvertPlcToZeroBased(r.RegisterAddress);
                    return r;
                })
                .OrderBy(r => r.RegisterAddress)
                .ToList();

            if (!regs.Any()) continue;

            var groups = ModbusRegisterGrouping.GroupContiguous(regs, r => r.RegisterAddress);

            foreach (var chunk in groups)
            {
                ushort start = (ushort)chunk[0].RegisterAddress;

                // FIX 1: count total words needed — Float32 needs 2 words per register
                ushort count = (ushort)chunk.Sum(r => WordCount(r.DataType));

                if (start + count > 65535)
                {
                    _log.LogError("Invalid Modbus range {Start}-{End}", start, start + count - 1);
                    continue;
                }

                ushort[] values;
                try
                {
                    values = master.ReadHoldingRegisters((byte)slave.SlaveIndex, start, count);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Slave {Slave} rejected range {Start}-{End}",
                        slave.SlaveIndex, start, start + count - 1);
                    continue;
                }

                // FIX 2: walk through words with an offset, decode per DataType
                int offset = 0;
                foreach (var reg in chunk)
                {
                    int wc = WordCount(reg.DataType);
                    if (offset + wc > values.Length)
                    {
                        _log.LogWarning("Not enough registers at addr {Addr}", reg.RegisterAddress);
                        break;
                    }

                    double value = DecodeRegister(reg.DataType, values, offset, reg.Scale);

                    // Debug: shows raw hex + decoded value so you can verify
                    //_log.LogDebug(
                    //    "  Addr={Addr}  Type={Type}  Raw=[{Raw}]  Value={Val}",
                    //    reg.RegisterAddress,
                    //    reg.DataType ?? "UInt16",
                    //    string.Join(",", values.Skip(offset).Take(wc).Select(v => v.ToString("X4"))),
                    //    value);

                    payloads.Add(new TelemetryPayload(
                        reg.SignalId!.Value.ToString(),
                        value,
                        now));

                    offset += wc;
                }

                await Task.Delay(30, ct); // RTU silent gap
            }
        }

        if (!payloads.Any()) return;

        await _influxDb.WriteAsync(payloads, ct);
        PrintConsole(device, payloads, now);
    }

    // Returns how many 16-bit words a DataType occupies
    private static int WordCount(string? dataType) =>
        dataType?.ToUpperInvariant() is "FLOAT32" or "FLOAT32AB" or "FLOAT32BA" ? 2 : 1;

    // Decodes raw register words to a double value.
    //
    // Set DataType in your register config to one of:
    //   "UInt16"    – unsigned 16-bit × Scale  (default when blank)
    //   "Int16"     – signed   16-bit × Scale
    //   "Float32"   – IEEE-754 float, high word first  (most PLCs — try this first)
    //   "Float32AB" – same as Float32
    //   "Float32BA" – IEEE-754 float, low word first   (if Float32 gives wrong value, use this)
    private static double DecodeRegister(string? dataType, ushort[] words, int offset, double scale)
    {
        return dataType?.ToUpperInvariant() switch
        {
            "INT16"                    => (short)words[offset] * scale,
            "FLOAT32" or "FLOAT32AB"  => RegsToFloat(hi: words[offset],     lo: words[offset + 1]),
            "FLOAT32BA"               => RegsToFloat(hi: words[offset + 1], lo: words[offset]),
            _                          => words[offset] * scale,   // UInt16 default
        };
    }

    private static float RegsToFloat(ushort hi, ushort lo)
    {
        uint raw = ((uint)hi << 16) | lo;
        return BitConverter.ToSingle(BitConverter.GetBytes(raw), 0);
    }

    private static int ConvertPlcToZeroBased(int plcAddress)
    {
        if (plcAddress >= 40001 && plcAddress <= 49999)
            return plcAddress - 40001;
        return plcAddress;
    }

    private void PrintConsole(
        DeviceConfigurationDto device,
        List<TelemetryPayload> payloads,
        DateTime timestamp)
    {
        lock (_consoleLock)
        {
            Console.WriteLine();
            //Console.WriteLine(new string('=', 70));
            //Console.WriteLine($"Device    : {device.DeviceName}");
            //Console.WriteLine($"Timestamp : {timestamp:yyyy-MM-dd HH:mm:ss} UTC");
            //Console.WriteLine($"Protocol  : Modbus RTU");
            //Console.WriteLine($"Payloads  : {payloads.Count} → InfluxDB");
            //Console.WriteLine(new string('-', 70));
            Console.WriteLine($"  {"RTU",-38} {"Value",10}");
            //Console.WriteLine(new string('-', 70));

            foreach (var p in payloads.Take(12))
                Console.WriteLine($"  {p.SignalId,-38} {p.Value,10:G6}");

            //if (payloads.Count > 12)
            //    Console.WriteLine($"  ... and {payloads.Count - 12} more");

            //Console.WriteLine(new string('=', 70));
        }
    }
}

public static class ModbusRegisterGrouping
{
    public static IEnumerable<List<T>> GroupContiguous<T>(
        IEnumerable<T> source,
        Func<T, int> addrSelector)
    {
        List<T> group = new();
        int? last = null;

        foreach (var item in source.OrderBy(addrSelector))
        {
            int addr = addrSelector(item);

            if (last == null || addr == last + 1)
                group.Add(item);
            else
            {
                yield return group;
                group = new List<T> { item };
            }

            last = addr;
        }

        if (group.Count > 0)
            yield return group;
    }
}