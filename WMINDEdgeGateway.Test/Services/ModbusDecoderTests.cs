using Xunit;
using FluentAssertions;
using WMINDEdgeGateway.Infrastructure.Services;

namespace WMINDEdgeGateway.Tests.Services
{
    public class ModbusDecoderTests
    {
        // ════════════════════════════════════════════════════
        //  WordCount
        // ════════════════════════════════════════════════════

        [Theory]
        [InlineData("Float32", 2)]
        [InlineData("FLOAT32", 2)]
        [InlineData("Float32AB", 2)]
        [InlineData("Float32BA", 2)]
        [InlineData("UInt16", 1)]
        [InlineData("Int16", 1)]
        [InlineData(null, 1)]
        [InlineData("", 1)]
        public void WordCount_ShouldReturnCorrectWordSize(string? dataType, int expected)
        {
            // Act
            int result = ModbusDecoder.WordCount(dataType);

            // Assert
            result.Should().Be(expected);
        }

        // ════════════════════════════════════════════════════
        //  ConvertPlcToZeroBased
        // ════════════════════════════════════════════════════

        [Fact]
        public void ConvertPlcToZeroBased_40001_ShouldReturnZero()
        {
            // 40001 is the first holding register in PLC addressing
            // Zero-based Modbus address = 40001 - 40001 = 0
            int result = ModbusDecoder.ConvertPlcToZeroBased(40001);
            result.Should().Be(0);
        }

        [Fact]
        public void ConvertPlcToZeroBased_40002_ShouldReturnOne()
        {
            int result = ModbusDecoder.ConvertPlcToZeroBased(40002);
            result.Should().Be(1);
        }

        [Fact]
        public void ConvertPlcToZeroBased_40010_ShouldReturn9()
        {
            int result = ModbusDecoder.ConvertPlcToZeroBased(40010);
            result.Should().Be(9);
        }

        [Fact]
        public void ConvertPlcToZeroBased_40100_ShouldReturn99()
        {
            int result = ModbusDecoder.ConvertPlcToZeroBased(40100);
            result.Should().Be(99);
        }

        [Fact]
        public void ConvertPlcToZeroBased_49999_ShouldReturn9998()
        {
            // Last valid PLC holding register address
            int result = ModbusDecoder.ConvertPlcToZeroBased(49999);
            result.Should().Be(9998);
        }

        [Fact]
        public void ConvertPlcToZeroBased_BelowRange_ShouldReturnSameValue()
        {
            // Address 100 is NOT in 40001-49999 range → return as-is
            int result = ModbusDecoder.ConvertPlcToZeroBased(100);
            result.Should().Be(100);
        }

        [Fact]
        public void ConvertPlcToZeroBased_Zero_ShouldReturnZero()
        {
            int result = ModbusDecoder.ConvertPlcToZeroBased(0);
            result.Should().Be(0);
        }

        [Fact]
        public void ConvertPlcToZeroBased_AboveRange_ShouldReturnSameValue()
        {
            // 50000 is above 49999 → return as-is
            int result = ModbusDecoder.ConvertPlcToZeroBased(50000);
            result.Should().Be(50000);
        }

        // ════════════════════════════════════════════════════
        //  DecodeRegister — UInt16 (default)
        // ════════════════════════════════════════════════════

        [Fact]
        public void DecodeRegister_UInt16_ScaleOne_ShouldReturnRawValue()
        {
            ushort[] words = { 250 };
            double result = ModbusDecoder.DecodeRegister("UInt16", words, 0, scale: 1.0);
            result.Should().Be(250.0);
        }

        [Fact]
        public void DecodeRegister_UInt16_WithScale_ShouldMultiplyByScale()
        {
            ushort[] words = { 100 };
            double result = ModbusDecoder.DecodeRegister("UInt16", words, 0, scale: 0.1);
            result.Should().BeApproximately(10.0, precision: 0.0001);
        }

        [Fact]
        public void DecodeRegister_UInt16_ZeroValue_ShouldReturnZero()
        {
            ushort[] words = { 0 };
            double result = ModbusDecoder.DecodeRegister("UInt16", words, 0, scale: 1.0);
            result.Should().Be(0.0);
        }

        [Fact]
        public void DecodeRegister_NullDataType_ShouldDefaultToUInt16Behavior()
        {
            ushort[] words = { 50 };
            double result = ModbusDecoder.DecodeRegister(null, words, 0, scale: 1.0);
            result.Should().Be(50.0);
        }

        [Fact]
        public void DecodeRegister_EmptyDataType_ShouldDefaultToUInt16Behavior()
        {
            ushort[] words = { 75 };
            double result = ModbusDecoder.DecodeRegister("", words, 0, scale: 1.0);
            result.Should().Be(75.0);
        }

        [Fact]
        public void DecodeRegister_UInt16_WithOffset_ShouldReadCorrectWord()
        {
            // offset=1 means read from second element
            ushort[] words = { 999, 123 };
            double result = ModbusDecoder.DecodeRegister("UInt16", words, offset: 1, scale: 1.0);
            result.Should().Be(123.0);
        }

        // ════════════════════════════════════════════════════
        //  DecodeRegister — Int16
        // ════════════════════════════════════════════════════

        [Fact]
        public void DecodeRegister_Int16_PositiveValue_ShouldWork()
        {
            ushort[] words = { 200 };
            double result = ModbusDecoder.DecodeRegister("Int16", words, 0, scale: 1.0);
            result.Should().Be(200.0);
        }

        [Fact]
        public void DecodeRegister_Int16_NegativeValue_ShouldWork()
        {
            // -50 as two's complement ushort
            ushort[] words = { unchecked((ushort)(-50)) };
            double result = ModbusDecoder.DecodeRegister("Int16", words, 0, scale: 1.0);
            result.Should().Be(-50.0);
        }

        [Fact]
        public void DecodeRegister_Int16_NegativeWithScale_ShouldApplyScale()
        {
            ushort[] words = { unchecked((ushort)(-100)) };
            double result = ModbusDecoder.DecodeRegister("Int16", words, 0, scale: 0.5);
            result.Should().BeApproximately(-50.0, precision: 0.0001);
        }

        [Fact]
        public void DecodeRegister_Int16_MaxNegative_ShouldWork()
        {
            // -32768 is the minimum value for Int16
            ushort[] words = { 0x8000 };
            double result = ModbusDecoder.DecodeRegister("Int16", words, 0, scale: 1.0);
            result.Should().Be(-32768.0);
        }

        // ════════════════════════════════════════════════════
        //  DecodeRegister — Float32 / Float32AB
        // ════════════════════════════════════════════════════

        [Fact]
        public void DecodeRegister_Float32_ShouldDecodeKnownValue()
        {
            // Encode 100.0f → two ushorts (big-endian, high word first)
            float expected = 100.0f;
            uint raw = BitConverter.ToUInt32(BitConverter.GetBytes(expected), 0);
            ushort hi = (ushort)(raw >> 16);
            ushort lo = (ushort)(raw & 0xFFFF);
            ushort[] words = { hi, lo };

            double result = ModbusDecoder.DecodeRegister("Float32", words, 0, scale: 1.0);

            result.Should().BeApproximately(100.0, precision: 0.001);
        }

        [Fact]
        public void DecodeRegister_Float32_Pi_ShouldDecodeCorrectly()
        {
            float expected = 3.14159f;
            uint raw = BitConverter.ToUInt32(BitConverter.GetBytes(expected), 0);
            ushort hi = (ushort)(raw >> 16);
            ushort lo = (ushort)(raw & 0xFFFF);
            ushort[] words = { hi, lo };

            double result = ModbusDecoder.DecodeRegister("Float32", words, 0, scale: 1.0);

            result.Should().BeApproximately(3.14159, precision: 0.0001);
        }

        [Fact]
        public void DecodeRegister_Float32AB_ShouldBeSameAsFloat32()
        {
            // Float32AB is an alias for Float32 — same word order
            float expected = 3.14f;
            uint raw = BitConverter.ToUInt32(BitConverter.GetBytes(expected), 0);
            ushort hi = (ushort)(raw >> 16);
            ushort lo = (ushort)(raw & 0xFFFF);
            ushort[] words = { hi, lo };

            double resultFloat32 = ModbusDecoder.DecodeRegister("Float32", words, 0, 1.0);
            double resultFloat32AB = ModbusDecoder.DecodeRegister("Float32AB", words, 0, 1.0);

            resultFloat32.Should().BeApproximately(resultFloat32AB, precision: 0.0001);
        }

        [Fact]
        public void DecodeRegister_Float32_NegativeValue_ShouldDecodeCorrectly()
        {
            float expected = -273.15f;
            uint raw = BitConverter.ToUInt32(BitConverter.GetBytes(expected), 0);
            ushort hi = (ushort)(raw >> 16);
            ushort lo = (ushort)(raw & 0xFFFF);
            ushort[] words = { hi, lo };

            double result = ModbusDecoder.DecodeRegister("Float32", words, 0, scale: 1.0);

            result.Should().BeApproximately(-273.15, precision: 0.001);
        }

        // ════════════════════════════════════════════════════
        //  DecodeRegister — Float32BA (swapped word order)
        // ════════════════════════════════════════════════════

        [Fact]
        public void DecodeRegister_Float32BA_ShouldSwapWordsCorrectly()
        {
            // Float32BA = lo word first, hi word second
            float expected = 55.5f;
            uint raw = BitConverter.ToUInt32(BitConverter.GetBytes(expected), 0);
            ushort hi = (ushort)(raw >> 16);
            ushort lo = (ushort)(raw & 0xFFFF);

            // BA order: lo comes first in array
            ushort[] words = { lo, hi };

            double result = ModbusDecoder.DecodeRegister("Float32BA", words, 0, scale: 1.0);

            result.Should().BeApproximately(55.5, precision: 0.001);
        }

        [Fact]
        public void DecodeRegister_Float32_And_Float32BA_GivenSameWords_ShouldReturnDifferentValues()
        {
            // This confirms Float32 and Float32BA are NOT the same
            ushort[] words = { 0x4248, 0x0000 }; // some float bytes

            double resultAB = ModbusDecoder.DecodeRegister("Float32", words, 0, 1.0);
            double resultBA = ModbusDecoder.DecodeRegister("Float32BA", words, 0, 1.0);

            resultAB.Should().NotBeApproximately(resultBA, precision: 0.001);
        }

        // ════════════════════════════════════════════════════
        //  RegsToFloat
        // ════════════════════════════════════════════════════

        [Fact]
        public void RegsToFloat_KnownBytes_ShouldReturnCorrectFloat()
        {
            // 100.0f in IEEE 754 = 0x42C80000
            // hi = 0x42C8, lo = 0x0000
            float result = ModbusDecoder.RegsToFloat(hi: 0x42C8, lo: 0x0000);
            result.Should().BeApproximately(100.0f, precision: 0.001f);
        }

        [Fact]
        public void RegsToFloat_Zero_ShouldReturnZero()
        {
            float result = ModbusDecoder.RegsToFloat(hi: 0x0000, lo: 0x0000);
            result.Should().Be(0.0f);
        }

        [Fact]
        public void RegsToFloat_RoundTrip_ShouldMatchOriginal()
        {
            // Encode a float → extract hi/lo → decode back → should match
            float original = 1234.56f;
            uint raw = BitConverter.ToUInt32(BitConverter.GetBytes(original), 0);
            ushort hi = (ushort)(raw >> 16);
            ushort lo = (ushort)(raw & 0xFFFF);

            float result = ModbusDecoder.RegsToFloat(hi, lo);

            result.Should().BeApproximately(original, precision: 0.001f);
        }
    }
}