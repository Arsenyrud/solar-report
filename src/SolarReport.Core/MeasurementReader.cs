using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SolarReport.Core;

public static class MeasurementReader
{
    public static IReadOnlyList<Measurement> Read(string path, bool requireTime = true)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            text = Encoding.GetEncoding(1251).GetString(bytes);
        }
        return Parse(text, requireTime);
    }

    public static IReadOnlyList<Measurement> Parse(string text, bool requireTime = true)
    {
        var rows = new List<Measurement>();
        using var reader = new StringReader(text);
        int lineNumber = 0;
        bool headerSeen = false;
        bool metadataSeen = false;
        while (reader.ReadLine() is { } rawLine)
        {
            lineNumber++;
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (rows.Count == 0 && !metadataSeen && Regex.IsMatch(line, @"^\[\s*0\s*,\s*0\s*,\s*0\s*\]$"))
            {
                metadataSeen = true;
                continue;
            }
            if (rows.Count == 0 && !headerSeen && line.StartsWith("Voltage", StringComparison.OrdinalIgnoreCase)
                && line.Contains("Current", StringComparison.OrdinalIgnoreCase))
            {
                headerSeen = true;
                continue;
            }
            var columns = Regex.Split(line, @"[\s;]+");
            if (requireTime ? columns.Length != 3 : columns.Length is < 2 or > 3)
                throw new FormatException($"Строка {lineNumber}: нужно {(requireTime ? "три столбца: напряжение, ток и время" : "два или три числовых столбца")}");
            var numbers = new double[columns.Length];
            for (int i = 0; i < columns.Length; i++)
            {
                if (!double.TryParse(columns[i].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])
                    || !double.IsFinite(numbers[i]))
                    throw new FormatException($"Строка {lineNumber}, столбец {i + 1}: неверное число «{columns[i]}»");
            }
            rows.Add(new(numbers[0], numbers[1], numbers.Length == 3 ? numbers[2] : null));
            if (rows.Count > 32767)
                throw new FormatException("В файле больше 32767 точек, этот шаблон XLS не поддерживает такую длину диаграммы");
        }
        if (rows.Count == 0)
            throw new FormatException("В файле нет измерений");
        return rows;
    }
}
