using SolarReport.Core;
using Xunit;

namespace SolarReport.Tests;

public sealed class CurveMathTests
{
    [Fact]
    public void UsesThreePointRegressionRatherThanTwoPointInterpolation()
    {
        var x = new double[] { -2, -1, 1 };
        var y = new double[] { 1, 2, 5 };
        Assert.Equal(25.0 / 7, CurveMath.Intercept(x, y, new(0, 3)), 12);
    }

    [Theory]
    [InlineData(2, 2, 2)]
    [InlineData(3, 1, 3)]
    [InlineData(10, 0, 6)]
    public void SelectsAdjacentPointsAtSignChange(int count, int start, int length) =>
        Assert.Equal(new DataWindow(start, length), CurveMath.ZeroWindow(new double[] { -3, -2, -1, 1, 2, 3 }, count));

    [Fact]
    public void ClipsWindowAtCurveBoundary() =>
        Assert.Equal(new DataWindow(0, 3), CurveMath.ZeroWindow(new double[] { 0, 1, 2, 3 }, 3));

    [Fact]
    public void RejectsConstantRegressionAxis() =>
        Assert.Throws<InvalidDataException>(() => CurveMath.Intercept(new double[] { 1, 1, 1 }, new double[] { 1, 2, 3 }, new(0, 3)));

    [Fact]
    public void RejectsMissingCrossing() =>
        Assert.Throws<InvalidDataException>(() => CurveMath.ZeroWindow(new double[] { 1, 2, 3 }, 2));
}
