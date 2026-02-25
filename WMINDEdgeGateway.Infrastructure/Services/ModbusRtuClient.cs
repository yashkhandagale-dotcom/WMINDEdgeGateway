using System.IO.Ports;

/// <summary>
/// Yeh file ek low-level helper hai jo physically serial port pe jaake Modbus RTU device se baat karta hai. 
///Poller service isko call karti hai — "yeh registers padhke do" — aur yeh sab kuch handle karta hai:
///frame banana, bhejhna, response padhna, validate karna.
///</summary>
namespace WMINDEdgeGateway.Infrastructure.Services
{
    /// <summary>
    /// Low-level Modbus RTU client — serial port pe CRC-framed requests bhejta hai.
    /// RS-485 bus shared hai — SemaphoreSlim ensure karta hai ek waqt pe sirf ek request.
    /// </summary>
    public static class ModbusRtuClient
    {
        // RS-485 bus ek waqt pe sirf ek conversation allow karta hai , traffic light rakhne ke liye.
        // SemaphoreSlim se yeh ensure hota hai. max count 1 hai, matlab ek hi thread request bhej sakta hai. Baaki wait karenge.
        private static readonly SemaphoreSlim _busSemaphore = new(1, 1);

        /// <summary>
        /// FC03 — Read Holding Registers
        /// </summary>
        public static async Task<ushort[]> ReadHoldingRegistersAsync(
            SerialPort port,
            byte slaveAddress,
            ushort startRegister,
            ushort count,
            CancellationToken ct)
        {   // Bus pe baat karne se pehle, traffic light pe red signal dekh ke ruk jao.
            await _busSemaphore.WaitAsync(ct);
            try
            {
                // Request frame banao 
                byte[] request = BuildRequest(slaveAddress, 0x03, startRegister, count);

                // Buffer clear karo taaki koi purana data interfere na kare.
                port.DiscardInBuffer();
                port.DiscardOutBuffer();

                // Request bhejo wire pe — yeh async hai taaki thread block na ho.
                await port.BaseStream.WriteAsync(request, 0, request.Length, ct);

                // Expected length calculate karo: 1(addr) + 1(fc) + 1(byteCount) + count*2(data) + 2(CRC)
                int expectedLength = 5 + count * 2;
                byte[] response = await ReadExactAsync(port, expectedLength, ct);

                // Validate check karo: address match, function code, CRC match, aur exception response check karo.
                ValidateResponse(response, slaveAddress, 0x03);

                // Response byte se actual register values extract karo. Data bytes start at index 3, aur har register 2 bytes ka hota hai.
                var registers = new ushort[count];
                for (int i = 0; i < count; i++)
                    registers[i] = (ushort)((response[3 + i * 2] << 8) | response[4 + i * 2]);
                // combine high and low byte to form the ushort value
                return registers;
            }
            finally
            {
                // Inter-frame gap — next request se pehle thoda ruko silence maintain karne ke liye.
                // Modbus RTU specification ke hisaab se, 3.5 character times ka gap hona chahiye.
                await Task.Delay(5, CancellationToken.None);
                _busSemaphore.Release(); // Traffic light green karo, next request ke liye.
            }
        }

        private static byte[] BuildRequest(
            byte slaveAddr, byte functionCode,
            ushort startReg, ushort count)
        {   //Yeh 6-byte PDU (Protocol Data Unit) hai — bina CRC ke. CRC baad mein calculate karke add karenge.
            var pdu = new byte[]
            {
                slaveAddr,
                functionCode,
                (byte)(startReg >> 8),   // High byte pehle
                (byte)(startReg & 0xFF), // Low byte
                (byte)(count >> 8),
                (byte)(count & 0xFF)
            };

            // CRC calculate karo PDU ke upar, aur usko PDU ke end mein add karo.
            // Modbus RTU mein CRC low byte pehle aata hai.
            ushort crc = CalculateCrc(pdu, 6);

            // Final frame return karo: PDU + CRC
            return new byte[]
            {
                pdu[0], pdu[1], pdu[2], pdu[3], pdu[4], pdu[5],
                (byte)(crc & 0xFF),  // CRC Low byte first (Modbus RTU rule)
                (byte)(crc >> 8)     // CRC High byte
            };
        }

        private static async Task<byte[]> ReadExactAsync(
            SerialPort port, int count, CancellationToken ct)
        {
            // SerialPort ka ReadAsync method guarantee nahi karta ki requested bytes ek hi call mein milenge. 
            // Isliye, hum ek loop mein read karenge jab tak required bytes nahi mil jati. Timeout bhi handle karenge taaki infinite wait na ho.
            // Buffer allocate karo jisme response store hoga aur total read count track karo.
            var buffer = new byte[count];
            int totalRead = 0;

            // Timeout implement karne ke liye, ek linked cancellation token source banao jo caller ke token ke saath ek timeout token combine kare. Agar timeout hota hai,
            // toh OperationCanceledException throw hoga jise hum catch karke TimeoutException mein convert karenge.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            // Loop chalao jab tak required bytes nahi mil jati. Har read attempt mein, remaining bytes calculate karo aur buffer ke correct offset pe store karo.
            while (totalRead < count)
            {
                try
                {
                    // buffer mein totalRead offset se count - totalRead bytes read karne ki koshish karo.
                    //aur linked token pass karo taaki timeout ya cancellation dono handle ho sake.
                    int read = await port.BaseStream.ReadAsync(
                        buffer, totalRead, count - totalRead, linked.Token);

                    if (read == 0)
                        throw new TimeoutException("Slave ne koi response nahi diya.");

                    totalRead += read;
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Response timeout — sirf {totalRead}/{count} bytes mile.");
                }
            }

            return buffer;
        }
        //  Response validate karo: address match, function code match, exception response check karo, aur CRC validate karo.
        private static void ValidateResponse(byte[] response, byte slaveAddr, byte fc)
        {
            // Minimum length check karo: address(1) + function code(1) + byte count(1) + CRC(2) = 5 bytes minimum.
            // Agar yeh condition fail hoti hai, toh response incomplete hai.
            if (response.Length < 4)
                throw new InvalidDataException("Incomplete Response");

            // Address check karo: response ka first byte slave address hona chahiye. Agar mismatch hota hai,
            // toh ya toh wrong device se baat kar rahe hain ya framing issue hai.
            if (response[0] != slaveAddr)
                throw new InvalidDataException(
                    $"Slave address mismatch: expected {slaveAddr}, got {response[0]}");

            // Exception response detect karna: function code ka bit 7 set hota hai
            if ((response[1] & 0x80) != 0)
                throw new ModbusRtuException(slaveAddr, response[2]);

            // Function code check karo: response ka function code request ke function code ke barabar hona chahiye. Agar mismatch hota hai,
            if (response[1] != fc)
                throw new InvalidDataException(
                    $"Function code mismatch: expected 0x{fc:X2}, got 0x{response[1]:X2}");

            // CRC validate karo
            // cshap mein, response ke last 2 bytes CRC hote hain. Unko combine karke received CRC value banao.
            // response[^2] mtlb second last byte, response[^1] mtlb last byte.
            // CRC low byte pehle aata hai, toh low byte ko first shift karo aur high byte ko add karo.
            ushort receivedCrc = (ushort)((response[^1] << 8) | response[^2]);
            ushort calculatedCrc = CalculateCrc(response, response.Length - 2);

            // CRC mismatch hone par exception throw karo. CRC error indicate karta hai ki data transmission mein corruption hua hai.
            if (receivedCrc != calculatedCrc)
                throw new InvalidDataException(
                    $"CRC mismatch: received 0x{receivedCrc:X4}, calculated 0x{calculatedCrc:X4}");
        }

        // Modbus RTU CRC-16 calculation algo (IBM algo). Yeh standard algorithm hai jo Modbus RTU frames ke integrity check ke liye use hota hai.
        public static ushort CalculateCrc(byte[] data, int length)
        {
            // CRC initial value 0xFFFF se start hota hai. Har byte ke liye, CRC ko byte ke saath XOR karo,
            // aur phir 8 times shift karo.
            // Agar least significant bit set hai, toh CRC ko polynomial 0xA001 ke saath XOR karo.
            ushort crc = 0xFFFF;
            for (int i = 0; i < length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x0001) != 0)
                        crc = (ushort)((crc >> 1) ^ 0xA001);
                    else
                        crc >>= 1;
                }
            }
            return crc;
        }
    }

    // Modbus RTU exception class — agar slave ek error response bhejta hai, toh iss exception ko throw karo.
    // Isme slave address aur exception code include hoga.Poller service isko specifically catch kar sakti hai
    public class ModbusRtuException : Exception
    {
        public byte SlaveAddress { get; }
        public byte ExceptionCode { get; }

        public ModbusRtuException(byte slaveAddress, byte exceptionCode)
            : base($"Modbus RTU exception from slave {slaveAddress}: " +
                   $"code 0x{exceptionCode:X2} — {Describe(exceptionCode)}")
        {
            SlaveAddress = slaveAddress;
            ExceptionCode = exceptionCode;
        }

        private static string Describe(byte code) => code switch
        {
            0x01 => "Illegal Function",
            0x02 => "Illegal Data Address",
            0x03 => "Illegal Data Value",
            0x04 => "Slave Device Failure",
            0x05 => "Acknowledge",
            0x06 => "Slave Device Busy",
            0x08 => "Memory Parity Error",
            0x0A => "Gateway Path Unavailable",
            0x0B => "Gateway Target Failed to Respond",
            _ => "Unknown Exception"
        };
    }
}