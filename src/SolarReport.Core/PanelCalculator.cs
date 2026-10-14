namespace SolarReport.Core;

public static class PanelCalculator
{
    public static CalculationResult Calculate(IReadOnlyList<Measurement> measurements, double area,
        double referenceCurrentDensity = 0.038)
    {
        if (!double.IsFinite(area) || area <= 0)
            throw new ArgumentOutOfRangeException(nameof(area), "Площадь должна быть положительным числом в см²");
        if (!double.IsFinite(referenceCurrentDensity) || referenceCurrentDensity <= 0)
            throw new ArgumentOutOfRangeException(nameof(referenceCurrentDensity), "JEQE должен быть положительным числом в А/см²");
        if (measurements.Count < 3)
            throw new InvalidDataException("Для расчета нужны минимум три точки измерения");
        var voltage = measurements.Select(p => p.Voltage).ToArray();
        var density = measurements.Select(p => -p.Current / area).ToArray();
        var power = voltage.Zip(density, (u, j) => u * j).ToArray();
        if (voltage.Concat(density).Concat(power).Any(v => !double.IsFinite(v)))
            throw new InvalidDataException("Измерения выходят за допустимый числовой диапазон");
        DataWindow shortCircuitWindow;
        DataWindow openCircuitWindow;
        bool reverseSweep = voltage[0] > voltage[^1];
        var selectionVoltage = reverseSweep ? voltage.Reverse().ToArray() : voltage;
        var selectionDensity = reverseSweep ? density.Reverse().ToArray() : density;
        try { shortCircuitWindow = MapWindow(CurveMath.ZeroWindow(selectionVoltage, 10)); }
        catch (InvalidDataException e) { throw new InvalidDataException("Не найден переход напряжения через ноль: нужен диапазон около U=0", e); }
        try { openCircuitWindow = MapWindow(CurveMath.ZeroWindow(selectionDensity, 3)); }
        catch (InvalidDataException e) { throw new InvalidDataException("Не найден переход тока через ноль: нужен диапазон около I=0", e); }
        var shortCircuit = density.Skip(shortCircuitWindow.StartIndex).Take(shortCircuitWindow.Count).ToArray();
        double jsc = shortCircuit.Average();
        double voc = CurveMath.Intercept(density, voltage, openCircuitWindow);
        int maximumPowerIndex = Array.IndexOf(power, power.Max());
        double pmax = power[maximumPowerIndex];
        if (jsc <= 0 || voc <= 0 || pmax <= 0)
            throw new InvalidDataException("Не получается рассчитать параметры генерации: проверьте полярность тока и диапазон напряжения");
        double relativeDeviation = Math.Sqrt(shortCircuit.Sum(j => (j - jsc) * (j - jsc)) / (shortCircuit.Length - 1)) / jsc;
        double illumination = jsc / referenceCurrentDensity;
        double efficiency = pmax * 10 / illumination;
        double fillFactor = pmax / voc / jsc;
        if (new[] { relativeDeviation, illumination, efficiency, fillFactor }.Any(v => !double.IsFinite(v)))
            throw new InvalidDataException("Результат расчета выходит за числовой диапазон");
        return new(area, referenceCurrentDensity, jsc, relativeDeviation, voc, maximumPowerIndex, pmax,
            voltage[maximumPowerIndex], density[maximumPowerIndex], illumination, efficiency, fillFactor,
            shortCircuitWindow, openCircuitWindow);

        DataWindow MapWindow(DataWindow window) => reverseSweep
            ? new(measurements.Count - window.StartIndex - window.Count, window.Count)
            : window;
    }
}
