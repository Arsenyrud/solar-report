using SolarReport.Core;
using Xunit;

namespace SolarReport.Tests;

public sealed class PanelCalculatorTests
{
    [Fact]
    public void MatchesIndependentCalculationsForSuppliedMeasurements()
    {
        var rows = MeasurementReader.Read(Path.Combine(AppContext.BaseDirectory, "examples", "measurements.txt"));
        var result = PanelCalculator.Calculate(rows, 2.341);
        Assert.Equal(200, rows.Count);
        Assert.Equal(0.003307661565163101, result.ShortCircuitCurrentDensity, 14);
        Assert.Equal(1.3634838358058676, result.OpenCircuitVoltage, 12);
        Assert.Equal(0.0008495643658483086, result.RelativeStandardDeviation, 12);
        Assert.Equal(140, result.MaximumPowerIndex);
        Assert.Equal(0.97011637687683, result.MaximumPowerVoltage, 13);
        Assert.Equal(0.0027661732549547203, result.MaximumPowerCurrentDensity, 14);
        Assert.Equal(0.002683509975910261, result.MaximumPowerDensity, 14);
        Assert.Equal(0.08704372539902898, result.Illumination, 13);
        Assert.Equal(0.30829447655283804, result.Efficiency, 12);
        Assert.Equal(0.5950208083008977, result.FillFactor, 12);
        Assert.Equal(new DataWindow(2, 10), result.ShortCircuitWindow);
        Assert.Equal(new DataWindow(193, 3), result.OpenCircuitWindow);
    }

    [Fact]
    public void MaximumPowerCanBeTheLastMeasuredPoint()
    {
        var rows = new[] { new Measurement(-1, -4), new Measurement(0, -3), new Measurement(1, 1), new Measurement(3, -2) };
        var result = PanelCalculator.Calculate(rows, 1);
        Assert.Equal(3, result.MaximumPowerIndex);
        Assert.Equal(6, result.MaximumPowerDensity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidArea(double area) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PanelCalculator.Calculate(Sample(), area));

    [Fact]
    public void RejectsMissingVoltageCrossing() =>
        Assert.Throws<InvalidDataException>(() => PanelCalculator.Calculate(new[] { new Measurement(1, -1), new Measurement(2, -1), new Measurement(3, 1) }, 1));

    [Fact]
    public void RejectsMissingCurrentCrossing() =>
        Assert.Throws<InvalidDataException>(() => PanelCalculator.Calculate(new[] { new Measurement(-1, -1), new Measurement(0, -1), new Measurement(1, -1) }, 1));

    [Fact]
    public void AreaChangesCurrentDensityAndLeavesVoltageUnchanged()
    {
        var first = PanelCalculator.Calculate(Sample(), 1);
        var second = PanelCalculator.Calculate(Sample(), 2);
        Assert.Equal(first.ShortCircuitCurrentDensity / 2, second.ShortCircuitCurrentDensity, 12);
        Assert.Equal(first.OpenCircuitVoltage, second.OpenCircuitVoltage, 12);
        Assert.Equal(first.MaximumPowerVoltage, second.MaximumPowerVoltage, 12);
        Assert.Equal(first.Efficiency, second.Efficiency, 12);
    }

    [Fact]
    public void ReverseSweepHasTheSameVoltageAndPower()
    {
        var first = PanelCalculator.Calculate(Sample(), 1);
        var reversed = PanelCalculator.Calculate(Sample().Reverse().ToArray(), 1);
        Assert.Equal(first.OpenCircuitVoltage, reversed.OpenCircuitVoltage, 12);
        Assert.Equal(first.MaximumPowerDensity, reversed.MaximumPowerDensity, 12);
    }

    [Fact]
    public void ReverseNonlinearSweepUsesTheSameThreePhysicalPoints()
    {
        double[] voltage = [-.5, -.4, -.3, -.2, -.1, 0, .1, .2, .3, .4, .5, .6, .7, .8, 1];
        double[] density = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, .7, .5, .3, -.2, -1.2];
        var rows = voltage.Zip(density, (u, j) => new Measurement(u, -j)).ToArray();
        var ascending = PanelCalculator.Calculate(rows, 1);
        var descending = PanelCalculator.Calculate(rows.Reverse().ToArray(), 1);
        Assert.Equal(0.75384615384615383, ascending.OpenCircuitVoltage, 12);
        Assert.Equal(ascending.OpenCircuitVoltage, descending.OpenCircuitVoltage, 12);
        Assert.Equal(ascending.FillFactor, descending.FillFactor, 12);
    }

    private static Measurement[] Sample() =>
    [new(-1, -4), new(0, -3), new(1, -2), new(2, -1), new(3, 0), new(4, 1)];
}
