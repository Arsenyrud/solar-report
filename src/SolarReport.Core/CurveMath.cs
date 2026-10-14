namespace SolarReport.Core;

public static class CurveMath
{
    public static DataWindow ZeroWindow(IReadOnlyList<double> values, int count)
    {
        if (count < 2 || values.Count < 2)
            throw new InvalidDataException("Для поиска перехода через ноль нужны минимум две точки");
        for (int i = 0; i < values.Count; i++)
        {
            if (!double.IsFinite(values[i]))
                throw new InvalidDataException("Кривая содержит нечисловое значение");
        }
        int left = -1;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] == 0)
            {
                left = i == 0 ? 0 : i - 1;
                break;
            }
            if (i + 1 < values.Count && Math.Sign(values[i]) != Math.Sign(values[i + 1]))
            {
                left = i;
                break;
            }
        }
        if (left < 0)
            throw new InvalidDataException("Кривая не пересекает ноль");
        int length = Math.Min(count, values.Count);
        int start = Math.Clamp(left - (length - 1) / 2, 0, values.Count - length);
        return new(start, length);
    }

    public static double Intercept(IReadOnlyList<double> x, IReadOnlyList<double> y, DataWindow window)
    {
        if (x.Count != y.Count || window.Count < 2 || window.StartIndex < 0
            || window.StartIndex + window.Count > x.Count)
            throw new InvalidDataException("Некорректный диапазон линейной регрессии");
        double meanX = 0, meanY = 0;
        for (int i = window.StartIndex; i < window.StartIndex + window.Count; i++)
        {
            if (!double.IsFinite(x[i]) || !double.IsFinite(y[i]))
                throw new InvalidDataException("Диапазон регрессии содержит нечисловое значение");
            meanX += x[i] / window.Count;
            meanY += y[i] / window.Count;
        }
        double variance = 0, covariance = 0;
        for (int i = window.StartIndex; i < window.StartIndex + window.Count; i++)
        {
            double deltaX = x[i] - meanX;
            variance += deltaX * deltaX;
            covariance += deltaX * (y[i] - meanY);
        }
        if (variance == 0 || !double.IsFinite(variance) || !double.IsFinite(covariance))
            throw new InvalidDataException("Не получается построить регрессию: значения оси одинаковы или слишком велики");
        double result = meanY - covariance / variance * meanX;
        if (!double.IsFinite(result))
            throw new InvalidDataException("Результат регрессии выходит за числовой диапазон");
        return result;
    }
}
