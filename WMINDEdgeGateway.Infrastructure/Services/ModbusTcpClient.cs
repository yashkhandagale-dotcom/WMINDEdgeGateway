using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    public static class ModbusTcpClient
    {
        private static ushort _txId = 0;
        private static readonly object _lock = new();

        private static ushort NextTxId()
        {
            lock (_lock)
            {
                _txId++;
                return _txId;
            }
        }

        public static async Task<ushort[]> ReadHoldingRegistersAsync(
            TcpClient tcp,
            byte unitId,
            ushort startAddress,
            ushort quantity,
            CancellationToken ct)
        {
            if (tcp == null || !tcp.Connected)
                throw new InvalidOperationException("TcpClient must be connected");

            var stream = tcp.GetStream();
            ushort tx = NextTxId();

            byte[] req = new byte[12];

            // Transaction ID
            req[0] = (byte)(tx >> 8);
            req[1] = (byte)(tx & 0xFF);

            // Protocol ID = 0
            req[2] = 0;
            req[3] = 0;

            // Length = 6
            req[4] = 0;
            req[5] = 6;

            // Unit ID + Function
            req[6] = unitId;
            req[7] = 3;

            // Start address
            req[8] = (byte)(startAddress >> 8);
            req[9] = (byte)(startAddress & 0xFF);

            // Quantity
            req[10] = (byte)(quantity >> 8);
            req[11] = (byte)(quantity & 0xFF);

            await stream.WriteAsync(req, ct);

            // Read MBAP header
            byte[] header = new byte[7];
            await ReadExactAsync(stream, header, ct);

            ushort respTx = (ushort)((header[0] << 8) | header[1]);
            ushort proto = (ushort)((header[2] << 8) | header[3]);
            ushort len = (ushort)((header[4] << 8) | header[5]);

            if (respTx != tx) throw new InvalidOperationException("Transaction ID mismatch");
            if (proto != 0) throw new InvalidOperationException("Invalid Modbus protocol");

            int pduLen = len - 1;
            if (pduLen < 2) throw new InvalidOperationException("Invalid PDU length");

            byte[] pdu = new byte[pduLen];
            await ReadExactAsync(stream, pdu, ct);

            if ((pdu[0] & 0x80) != 0)
                throw new InvalidOperationException($"Modbus exception {pdu[1]}");

            int regCount = pdu[1] / 2;
            ushort[] regs = new ushort[regCount];

            for (int i = 0; i < regCount; i++)
            {
                int offset = 2 + i * 2;
                regs[i] = (ushort)((pdu[offset] << 8) | pdu[offset + 1]);
            }

            return regs;
        }

        private static async Task ReadExactAsync(
            NetworkStream stream,
            byte[] buffer,
            CancellationToken ct)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
                if (n == 0) throw new SocketException();
                read += n;
            }
        }
    }
}
