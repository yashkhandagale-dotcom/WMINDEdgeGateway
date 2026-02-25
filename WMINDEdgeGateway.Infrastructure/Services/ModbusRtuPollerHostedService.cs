using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO.Ports;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Infrastructure.Caching;


///Yeh file poller service hai — jo background mein continuously chalta rehta hai,
///RTU devices se data padhta hai, aur InfluxDB mein push karta hai. 
///ModbusRtuClient.cs sirf "ek request bhejo" jaanta tha, yeh service jaanti hai "kab, kisko, kya poochna hai 
///aur data ka kya karna hai."
namespace WMINDEdgeGateway.Infrastructure.Services
{
    // background service jo continuously RTU devices ko poll karta hai aur data InfluxDB mein push karta hai
    public class ModbusRtuPollerHostedService : BackgroundService
    {
        // main dependencies: logger, config for app setting, in-memory cache (device configs), InfluxDB service and lock for console output
        private readonly ILogger<ModbusRtuPollerHostedService> _log;
        private readonly IConfiguration _config;
        private readonly MemoryCacheService _cache;
        private readonly IInfluxDbService _influxDb;

        private static readonly object _consoleLock = new();

        // program.cs ne jo dependencies pass ki hain, unko constructor mein receive karo
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

            // Cache se device configurations uthao aur na mile toh return kar do
            var deviceConfigs = _cache.Get<List<DeviceConfigurationDto>>("ModbusRtuDevices");

            if (deviceConfigs == null || !deviceConfigs.Any())
            {
                _log.LogWarning("No Modbus RTU devices found in cache.");
                return;
            }

            // RS-485: devices ko COM port ke hisaab se group karo
            // Ek COM port = ek serial bus = serial (not parallel) polling
            // null or empty SerialPort wale devices ko ignore karo,com port ke hisaab se group karo aur dictionary banao
            // byPort = {
            //              "COM3" → [Device A, Device B],
            //              "COM7" → [Device C]
            //          }
           
            var byPort = deviceConfigs
                .Where(d => !string.IsNullOrWhiteSpace(d.SerialPort))
                .GroupBy(d => d.SerialPort!)
                .ToDictionary(g => g.Key, g => g.ToList());

            if (!byPort.Any())
            {
                _log.LogWarning("RTU devices mein SerialPort configured nahi hai.");
                return;
            }

            // Har COM port ke liye ek parallel task chalao jo us port ko poll karega
            var tasks = byPort.Select(kv =>
                Task.Run(() => PollPortAsync(kv.Key, kv.Value, stoppingToken), stoppingToken));

            // task complete hone ka wait karo (yeh tab tak chalega jab tak service stop nahi hoti)
            await Task.WhenAll(tasks);
        }

        private async Task PollPortAsync(
            string comPort,
            List<DeviceConfigurationDto> devices,
            CancellationToken ct)
        {
            // Same COM port pe jo devices hain, unka baud rate same hona chahiye (kyunki ek hi physical bus hai).
            // Isliye pehle device ki settings le li.
            var first = devices.First();

            //using ensure karta hai ki port.Dispose() automatically call hoga — chahe exception aaye ya normal exit.
            using var port = new SerialPort(comPort)
            {
                BaudRate = first.BaudRate ?? 9600,
                DataBits = _config.GetValue<int>("ModbusRtu:DataBits", 8),   // appsettings se, default 8
                Parity = ParseParity(first.Parity),
                StopBits = ParseStopBits(_config.GetValue<string>("ModbusRtu:StopBits", "1")), // default "1"
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
                _log.LogError(ex, "Cannot open serial port {Port}. Check port name and permissions.", comPort);
                return;
            }

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // COM port pe jitne devices hain, unko sequentially poll karo (RS-485 bus ke liye zaruri hai)
                    foreach (var device in devices)
                    {
                        if (ct.IsCancellationRequested) break;

                        try
                        {
                            await PollDeviceAsync(device, port, ct);
                        }
                        // PollDeviceAsync mein agar timeout ya Modbus exception aati hai ya koi aur,
                        // toh usko catch karo aur log karo,lekin loop continue karo
                        catch (TimeoutException)
                        {
                            _log.LogWarning("Timeout polling device {Device} on {Port}.",
                                device.DeviceName, comPort);
                        }
                        catch (ModbusRtuException mex)
                        {
                            _log.LogWarning("Modbus exception for {Device}: {Msg}",
                                device.DeviceName, mex.Message);
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "RTU poll error for device {Device}.", device.DeviceName);
                        }

                        // Slaves ke beech mein thoda gap — RS-485 bus ke liye (silent interval)
                        await Task.Delay(20, ct);
                    }

                    // Saare devices poll ho gaye — interval ke liye wait karo, jis devices mein sabse chhota poll interval ho
                    int interval = devices.Min(d => d.PollIntervalMs ?? 1000);
                    await Task.Delay(interval, ct);
                }
            }
            finally
            {
                try { port.Close(); } catch { }
                _log.LogInformation("Serial port {Port} closed.", comPort);
            }
        }

        // Ek device ko poll karne ka logic — uske slaves ke registers read karo, telemetry payloads banao,
        // aur InfluxDB mein push karo
        private async Task PollDeviceAsync(
            DeviceConfigurationDto device,
            SerialPort port,
            CancellationToken ct)
        {
            // Agar device ke slaves null hain ya koi slave nahi hai, toh return kar do
            if (device.Slaves == null || !device.Slaves.Any()) return;

            // Is device ke slaves ke registers read karo, unko telemetry payloads mein convert karo, aur InfluxDB mein push karo
            var payloads = new List<TelemetryPayload>();
            // Poore batch ke liye ek timestamp. Sab registers ka timestamp same hoga — ek poll cycle = ek point in time.
            var now = DateTime.UtcNow;

            // detect karo ki kya device ke registers 40001 ya usse upar ke addresses use kar rahe hain (1-based addressing)
            bool dbUses40001 = device.Slaves.Any(s =>
                s.Registers.Any(r => r.RegisterAddress >= 40001));

            // agr 40001-based addressing use ho rahi hai, toh usko 0-based mein convert karne ke liye ek helper function
            // nahi toh agar address 1-40000 ke beech mein hai, toh usko bhi 0-based mein convert karo (address - 1)
            int ToProto(int addr) =>
                dbUses40001 ? addr - 40001 :
                (addr > 0 && addr < 40001) ? addr - 1 : addr;

            // Har slave ke liye, uske active registers ko read karo (jinmein signalId hai), unko contiguous batches mein group karo,
            foreach (var slave in device.Slaves)
            {
                // Slave ke registers mein se sirf active registers ko consider karo (jinmein signalId hai aur wo valid hai),
                // unko register address ke hisaab se sort karo aur order by se batch karo (taaki contiguous addresses ek saath read ho sakein)
                var activeRegs = slave.Registers
                    .Where(r => r.SignalId.HasValue && r.SignalId != Guid.Empty)
                    .OrderBy(r => r.RegisterAddress)
                    .ToList();

                // Agar is slave ke paas koi active register nahi hai, toh usko skip karo
                if (!activeRegs.Any()) continue;

                // Registers ko contiguous ranges mein batch karo (max 125 per read)
                const int maxRegsPerRead = 125;
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
                    ushort count = (ushort)Math.Min(endProto - startProto + 2, maxRegsPerRead);

                    // 10 alag read requests ki jagah sirf 2 requests — bahut efficient!

                    // decode the batch of registers from this slave, convert to telemetry payloads, and add to the list
                    try
                    {
                        ushort[] regs = await ModbusRtuClient.ReadHoldingRegistersAsync(
                            port, (byte)slave.SlaveIndex, start, count, ct);

                        // Ab humare paas is batch ke registers ki values hain —
                        // ab unko telemetry payloads mein convert karte hain
                        for (int k = j; k <= batchEnd; k++)
                        {
                            var reg = activeRegs[k];
                            int idx = ToProto(reg.RegisterAddress) - startProto;

                            if (idx < 0 || idx >= regs.Length) continue;

                            double finalValue;

                            // Agar register ka DataType "float32" hai, toh uske liye 2 consecutive registers ko float mein convert karo
                            if (string.Equals(reg.DataType, "float32",
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                if (idx + 1 >= regs.Length) continue;

                                // 2 consecutive registers ko float32 mein convert karne ke liye, unke bytes ko sahi order mein arrange karo
                                ushort r1 = regs[idx], r2 = regs[idx + 1];
                                // Word swap agar configured hai, toh unka order change karo
                                byte[] bytes = reg.WordSwap
                                    ? new[] { (byte)(r2 >> 8), (byte)(r2 & 0xFF), (byte)(r1 >> 8), (byte)(r1 & 0xFF) }
                                    : new[] { (byte)(r1 >> 8), (byte)(r1 & 0xFF), (byte)(r2 >> 8), (byte)(r2 & 0xFF) };

                                // Ab bytes ko float mein convert karo
                                float raw = BitConverter.ToSingle(bytes, 0);

                                // Agar raw value NaN, Infinity, ya bahut badi hai (1e6 se zyada), toh usko ignore karke scaled register value use karo
                                finalValue = (float.IsNaN(raw) || float.IsInfinity(raw) || Math.Abs(raw) > 1e6)
                                    ? regs[idx] * reg.Scale
                                    : raw * reg.Scale;
                            }
                            else
                            {
                                finalValue = regs[idx] * reg.Scale;
                            }

                            // Ab humare paas is register ka final value hai — usko telemetry payload mein convert karo aur list mein add karo
                            payloads.Add(new TelemetryPayload(
                                reg.SignalId!.Value.ToString(),
                                finalValue,
                                now));
                        }
                    }
                    catch (TimeoutException)
                    {
                        _log.LogWarning("Timeout: slave {Slave} on device {Device}.",
                            slave.SlaveIndex, device.DeviceName);
                    }
                    catch (ModbusRtuException mex)
                    {
                        _log.LogWarning("Modbus ex: slave {Slave} — {Msg}",
                            slave.SlaveIndex, mex.Message);
                    }

                    j = batchEnd + 1;
                }
            }

            if (!payloads.Any()) return;

            // Ab is device ke saare registers read ho gaye hain, aur unke telemetry payloads ban gaye hain —
            // ab unko InfluxDB mein push karo
            await _influxDb.WriteAsync(payloads, ct);

            _log.LogInformation("RTU: Pushed {Count} points to InfluxDB for {Device}.",
                payloads.Count, device.DeviceName);

            // Console output ke liye lock use karo taaki multiple threads ek saath console pe likhne ki koshish na karein
            lock (_consoleLock)
            {
                Console.WriteLine();
                Console.WriteLine(new string('=', 65));
                Console.WriteLine($"Device    : {device.DeviceName} | {device.SerialPort}");
                Console.WriteLine($"Timestamp : {now:yyyy-MM-dd HH:mm:ss} UTC");
                Console.WriteLine($"Protocol  : Modbus RTU");
                Console.WriteLine($"Payloads  : {payloads.Count} → InfluxDB");
                Console.WriteLine(new string('-', 65));
                Console.WriteLine($"  {"SignalId",-38} {"Value",10}");
                Console.WriteLine(new string('-', 65));
                foreach (var p in payloads.Take(10))
                    Console.WriteLine($"  {p.SignalId,-38} {p.Value,10:G6}");
                if (payloads.Count > 10)
                    Console.WriteLine($"  ... and {payloads.Count - 10} more");
                Console.WriteLine(new string('=', 65));
            }
        }

        // SerialPort ke liye Parity aur StopBits ko parse karne ke helper functions
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