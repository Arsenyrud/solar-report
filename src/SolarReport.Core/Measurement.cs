namespace SolarReport.Core;

public sealed record Measurement(double Voltage, double Current, double? Time = null);

public sealed record DataWindow(int StartIndex, int Count)
{
    public int FirstExcelRow => StartIndex + 3;
    public int LastExcelRow => StartIndex + Count + 2;
}

public sealed record CalculationResult(
    double Area,
    double ReferenceCurrentDensity,
    double ShortCircuitCurrentDensity,
    double RelativeStandardDeviation,
    double OpenCircuitVoltage,
    int MaximumPowerIndex,
    double MaximumPowerDensity,
    double MaximumPowerVoltage,
    double MaximumPowerCurrentDensity,
    double Illumination,
    double Efficiency,
    double FillFactor,
    DataWindow ShortCircuitWindow,
    DataWindow OpenCircuitWindow);

public sealed record ReportRequest(
    string InputPath,
    string OutputPath,
    double Area,
    string? TemplatePath = null,
    double? ReferenceCurrentDensity = null,
    string? DarkInputPath = null,
    string? SintonInputPath = null);

public sealed record ReportResult(
    string OutputPath,
    int MeasurementCount,
    CalculationResult Calculations,
    IReadOnlyList<string> Warnings);
