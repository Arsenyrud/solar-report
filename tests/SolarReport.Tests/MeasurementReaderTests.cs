using SolarReport.Core;
using Xunit;

namespace SolarReport.Tests;

public sealed class MeasurementReaderTests
{
    [Fact]
    public void ReadsLabTracerWithoutTurningMetadataIntoAMeasurement()
    {
        var rows = MeasurementReader.Parse("[0, 0, 0]\nVoltage_1 (1)\tCurrent_1 (1)\tTime Ch_1 (1)\n-0.1\t-0.02\t0.1\n0\t-0.01\t0.2");
        Assert.Equal(2, rows.Count);
        Assert.Equal(new Measurement(-0.1, -0.02, 0.1), rows[0]);
        Assert.Equal(0, rows[1].Voltage);
    }

    [Theory]
    [InlineData("0,1;-2,5e-3;0,2")]
    [InlineData("0.1 -2.5e-3 0.2")]
    [InlineData("0.1\t-2.5E-3\t0.2")]
    public void ReadsNumericFormats(string text)
    {
        var row = Assert.Single(MeasurementReader.Parse(text));
        Assert.Equal(new Measurement(0.1, -0.0025, 0.2), row);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Voltage_1 (1)\tCurrent_1 (1)\tTime Ch_1 (1)")]
    [InlineData("0 1")]
    [InlineData("0 1 2 3")]
    [InlineData("0 NaN 1")]
    [InlineData("Infinity 0 1")]
    [InlineData("0 1 2\nbroken row")]
    [InlineData("0 1 2\nVoltage_1 (1) Current_1 (1) Time Ch_1 (1)")]
    public void RejectsMissingOrDamagedMeasurements(string text) =>
        Assert.Throws<FormatException>(() => MeasurementReader.Parse(text));

    [Fact]
    public void ReportsDamagedLineNumber()
    {
        var error = Assert.Throws<FormatException>(() => MeasurementReader.Parse("#comment\n0 1 2\n0 bad 3"));
        Assert.Contains("3", error.Message);
    }

    [Fact]
    public void ReadsTwoColumnAuxiliaryData() =>
        Assert.Equal(new Measurement(0.1, 0.02), Assert.Single(MeasurementReader.Parse("0.1 0.02", false)));

    [Fact]
    public void RetainsAnActualZeroRow() =>
        Assert.Equal(new Measurement(0, 0, 0), Assert.Single(MeasurementReader.Parse("0 0 0")));
}
