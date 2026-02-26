using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NModbus;
using NModbus.Serial;
using Opc.Ua;
using System.IO.Ports;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Infrastructure.Caching;

namespace WMINDEdgeGateway.Infrastructure.Services;

// this is the main Modbus RTU polling service. It reads device configs from cache,
// polls each serial port for its devices, decodes register values, and writes telemetry to InfluxDB.
public class ModbusRtuPollerHostedService : BackgroundService
{
    private readonly ILogger<ModbusRtuPollerHostedService> _log;
    private readonly IConfiguration _config;
    private readonly MemoryCacheService _cache;
    private readonly IInfluxDbService _influxDb;

    private static readonly object _consoleLock = new();

    // Constructor with dependency injection for logging, configuration, cache, and InfluxDB service
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

    // Main execution loop of the hosted service. It retrieves device configs from cache
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Modbus RTU Poller started.");

        var devices = _cache.Get<List<DeviceConfigurationDto>>("ModbusRtuDevices");
        if (devices == null || !devices.Any())
        {
            _log.LogWarning("No Modbus RTU devices configured.");
            return;
        }

        // Group devices by their SerialPort to poll each port in parallel

        var byPort = devices
            .Where(d => !string.IsNullOrWhiteSpace(d.SerialPort))
            .GroupBy(d => d.SerialPort!)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Start a polling task for each serial port
        var tasks = byPort.Select(kv =>
            Task.Run(() => PollPortAsync(kv.Key, kv.Value, stoppingToken), stoppingToken));

        // Wait for all polling tasks to complete (which will be when the service is stopped)
        await Task.WhenAll(tasks);
    }

    // Polls a single serial port for all its associated devices. It opens the port, creates a Modbus master,
    private async Task PollPortAsync(
        string portName,
        List<DeviceConfigurationDto> devices,
        CancellationToken ct)
    {
        // For simplicity, we take the first device's baud rate as the port setting. In a real implementation,
        var first = devices.First();

        using var port = new SerialPort(portName)
        {
            BaudRate     = first.BaudRate ?? 9600,
            DataBits     = 8, // most common setting for Modbus RTU
            Parity       = Parity.None,
            StopBits     = StopBits.One,
            ReadTimeout  = 3000, // 3 seconds timeout for reads
            WriteTimeout = 3000  // 3 seconds timeout for writes
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

        // Create Modbus master for RTU communication over the opened serial port
        // NModbus ka entry point
        // SerialPort ko NModbus - compatible banaata hai
        // CreateRtuMaster → RTU protocol ka master object banata hai —
        // yahi actual Modbus frames banayega aur parse karega
        var factory = new ModbusFactory();
        using var adapter = new SerialPortAdapter(port);
        using var master  = factory.CreateRtuMaster(adapter);

        master.Transport.Retries                 = 2;
        master.Transport.WaitToRetryMilliseconds = 200;

        while (!ct.IsCancellationRequested)
        {
            // Har device ko ek-ek karke poll karo.
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

                await Task.Delay(50, ct); //Har device ke baad 50ms wait.
            }
            // Port ke baad, jitna bhi devices hain unka minimum poll interval check karo, uske hisaab se wait karo.
            int delay = devices.Min(d => d.PollIntervalMs ?? 1000);
            await Task.Delay(delay, ct);
        }
    }

    // Polls a single device by reading its configured registers, decoding values,
    // and writing telemetry to InfluxDB.
    private async Task PollDeviceAsync(
        DeviceConfigurationDto device,
        IModbusSerialMaster master,
        CancellationToken ct)
    {
        if (device.Slaves == null || !device.Slaves.Any())
            return;

        // Payloads list to collect decoded telemetry before writing to InfluxDB
        var payloads = new List<TelemetryPayload>();
        var now      = DateTime.UtcNow; // Timestamp for all payloads from this poll

        // Har slave ke registers ko read karo, decode karo, aur payloads list me add karo.
        foreach (var slave in device.Slaves)
        {
            // Get registers that have a valid SignalId and convert their addresses to zero-based for Modbus and sort by address
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

            // Group contiguous registers together to minimize Modbus requests. Registers are already sorted by address.
            var groups = ModbusRegisterGrouping.GroupContiguous(regs, r => r.RegisterAddress);

            foreach (var chunk in groups)
            {
                // Phle register ka address lo 
                ushort start = (ushort)chunk[0].RegisterAddress;

                // kitne words padhne hain. Float32 ko 2 words chahiye (32 bits = 2×16 bits), baaki sab 1 word.
                ushort count = (ushort)chunk.Sum(r => WordCount(r.DataType));

                // check if the requested range exceeds Modbus limits (0-65535)
                if (start + count > 65535)
                {
                    _log.LogError("Invalid Modbus range {Start}-{End}", start, start + count - 1);
                    continue;
                }

                // Read the whole chunk of registers in one Modbus request
                ushort[] values;
                try
                {
                    // Read the entire range of registers for the chunk, not just one register
                    values = master.ReadHoldingRegisters((byte)slave.SlaveIndex, start, count);
                    
                    // yeh actual Modbus request hai! NModbus yahan:
                    //  Frame banata hai:
                    //  [SlaveID] [03][start_hi][start_lo][count_hi][count_lo][CRC_lo][CRC_hi]
                    //  Serial port pe bhejta hai
                    //  Response wait karta hai
                    //  CRC verify karta hai
                    //  Register values return karta hai
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Slave {Slave} rejected range {Start}-{End}",
                        slave.SlaveIndex, start, start + count - 1);
                    continue;
                }

                // Walk through words with an offset, decode per DataType
                int offset = 0;
                foreach (var reg in chunk)
                {
                    int wc = WordCount(reg.DataType);
                    if (offset + wc > values.Length)
                    {
                        _log.LogWarning("Not enough registers at addr {Addr}", reg.RegisterAddress);
                        break;
                    }

                    // Decode the raw register values to a double using the specified DataType and Scale
                    double value = DecodeRegister(reg.DataType, values, offset, reg.Scale);

                    // Add a telemetry payload for this register if it has a valid SignalId
                    payloads.Add(new TelemetryPayload(
                        reg.SignalId!.Value.ToString(),
                        value,
                        now));

                    // Move the offset by the number of words we just processed (1 for UInt16/Int16, 2 for Float32)
                    offset += wc;
                }

                await Task.Delay(30, ct); // RTU silent gap
            }
        }

        // check payloads list. Agar koi valid payloads hain to InfluxDB me likho, aur console pe print karo.
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

    // Combines two 16-bit register values into a 32-bit float.
    // The 'hi' and 'lo' parameters depend on the DataType (Float32 vs Float32BA).
    private static float RegsToFloat(ushort hi, ushort lo)
    {
        uint raw = ((uint)hi << 16) | lo;
        return BitConverter.ToSingle(BitConverter.GetBytes(raw), 0);
    }

    // converts PLC-style 1-based register addresses (e.g. 40001) to zero-based addresses for Modbus (e.g. 0)
    private static int ConvertPlcToZeroBased(int plcAddress)
    {
        if (plcAddress >= 40001 && plcAddress <= 49999)
            return plcAddress - 40001;
        return plcAddress;
    }

    // Simple console output to visualize the latest telemetry values for each device.
    // This is optional and can be removed in production.
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