namespace WMINDEdgeGateway.Infrastructure.Services
{
    public static class ModbusDecoder
    {
        public static double DecodeRegister(string? dataType, ushort[] words, int offset, double scale)
        {
            return dataType?.ToUpperInvariant() switch
            {
                "INT16" => (short)words[offset] * scale,
                "FLOAT32" or "FLOAT32AB" => RegsToFloat(hi: words[offset], lo: words[offset + 1])*scale,
                "FLOAT32BA" => RegsToFloat(hi: words[offset + 1], lo: words[offset])*scale,
                _ => words[offset] * scale
            };
        }

        public static float RegsToFloat(ushort hi, ushort lo)
        {
            uint raw = ((uint)hi << 16) | lo;
            return BitConverter.ToSingle(BitConverter.GetBytes(raw), 0);
        }

        public static int ConvertPlcToZeroBased(int plcAddress)
        {
            if (plcAddress >= 40001 && plcAddress <= 49999)
                return plcAddress - 40001;
            return plcAddress;
        }

        public static int WordCount(string? dataType) =>
            dataType?.ToUpperInvariant() is "FLOAT32" or "FLOAT32AB" or "FLOAT32BA" ? 2 : 1;
    }
}