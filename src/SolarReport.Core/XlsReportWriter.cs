using NPOI.HSSF.Record;
using NPOI.HSSF.Record.Chart;
using NPOI.HSSF.UserModel;
using NPOI.SS.Formula.PTG;
using NPOI.SS.UserModel;
using NPOI.SS.Util;

namespace SolarReport.Core;

public static class XlsReportWriter
{
    private const string SheetName = "С=0.7";
    private const string MissingData = "нет данных";
    private const int MaximumMeasurements = 65534;

    public static ReportResult Generate(ReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outputPath = ValidateOutputPath(request);
        var measurements = ReadMain(request.InputPath);
        using var workbook = LoadTemplate(request.TemplatePath);
        if (workbook.GetSheet(SheetName) is not HSSFSheet sheet)
            throw new InvalidDataException($"В шаблоне отсутствует лист «{SheetName}».");

        var referenceDensity = request.ReferenceCurrentDensity ?? NumericValue(sheet.GetRow(3)?.GetCell(7)) ?? 0.038;
        var calculations = PanelCalculator.Calculate(measurements, request.Area, referenceDensity);
        var warnings = new List<string>();
        if (CountZeroCrossings(measurements.Select(measurement => measurement.Current)) > 1)
            warnings.Add("В кривой несколько переходов тока через ноль, использован первый по возрастанию напряжения. Проверьте измерение.");
        if (CountZeroCrossings(measurements.Select(measurement => measurement.Voltage)) > 1)
            warnings.Add("В кривой несколько переходов напряжения через ноль. Проверьте измерение и выбранный диапазон Jsc.");
        var dark = string.IsNullOrWhiteSpace(request.DarkInputPath)
            ? ReadTemplateDark(sheet)
            : ReadOptional(request.DarkInputPath, "темновой ВАХ", warnings);
        var sinton = ReadOptional(request.SintonInputPath, "данных Синтон", warnings);
        var editor = new SheetEditor(sheet);
        editor.DetachArrays();
        editor.ClearColumns(0, 5);
        editor.ClearColumns(10, 17);
        editor.ClearColumns(19, 23);

        editor.Number("H1", calculations.Area);
        editor.Number("H4", referenceDensity);
        editor.Number("V2", calculations.Area);
        for (var index = 0; index < measurements.Count; index++)
        {
            var row = index + 3;
            var measurement = measurements[index];
            var density = -measurement.Current / calculations.Area;
            editor.Number(row, 0, measurement.Voltage);
            editor.Number(row, 1, measurement.Current);
            editor.Number(row, 2, measurement.Time!.Value);
            editor.Formula(row, 3, $"A{row}", measurement.Voltage);
            editor.Formula(row, 4, $"-B{row}/$H$1", density);
            editor.Formula(row, 5, $"D{row}*E{row}", measurement.Voltage * density);
        }
        WriteMainResults(editor, calculations, measurements.Count);
        var darkWindow = WriteDark(editor, dark, calculations, warnings);
        WriteSinton(editor, sinton, calculations, warnings);
        editor.EvaluateFormulas(workbook);
        var negativeDark = Enumerable.Range(22, 3).Any(row => NumericValue(sheet.GetRow(row)?.GetCell(7)) < 0);
        var negativeSinton = NumericValue(sheet.GetRow(25)?.GetCell(7)) < 0;
        if (negativeDark || negativeSinton)
        {
            var source = negativeDark && negativeSinton ? "темновой ВАХ и данным Синтон"
                : negativeDark ? "темновой ВАХ" : "данным Синтон";
            warnings.Add($"Получены отрицательные сопротивления по {source}. Проверьте, что все ВАХ сняты для одной панели.");
        }
        UpdateChartRanges(workbook, sheet, measurements.Count, dark?.Count ?? 0, sinton?.Count ?? 0, darkWindow,
            string.IsNullOrWhiteSpace(request.DarkInputPath));
        UpdateChartCaches(workbook, sheet);
        FitMainChartAxes(sheet, calculations);
        FormatMainChartAxes(workbook, sheet);
        sheet.ForceFormulaRecalculation = true;
        SaveAtomically(workbook, outputPath);
        return new ReportResult(outputPath, measurements.Count, calculations, warnings.AsReadOnly());
    }

    private static void WriteMainResults(SheetEditor editor, CalculationResult result, int count)
    {
        var lastRow = count + 2;
        var shortCircuit = result.ShortCircuitWindow;
        var openCircuit = result.OpenCircuitWindow;
        editor.Formula("H5", $"AVERAGE(E{shortCircuit.FirstExcelRow}:E{shortCircuit.LastExcelRow})", result.ShortCircuitCurrentDensity);
        editor.Formula("I5", $"STDEV(E{shortCircuit.FirstExcelRow}:E{shortCircuit.LastExcelRow})/H5", result.RelativeStandardDeviation);
        editor.Formula("H6", $"INTERCEPT(D{openCircuit.FirstExcelRow}:D{openCircuit.LastExcelRow},E{openCircuit.FirstExcelRow}:E{openCircuit.LastExcelRow})", result.OpenCircuitVoltage);
        editor.Formula("H14", "H5/H4", result.Illumination);
        editor.Formula("H15", "H4", result.ReferenceCurrentDensity);
        editor.Formula("H16", $"MAX(F3:F{lastRow})*10/H14", result.Efficiency);
        editor.Formula("H17", "H6", result.OpenCircuitVoltage);
        editor.Formula("H18", "H5", result.ShortCircuitCurrentDensity);
        editor.Formula("H19", $"MAX(F3:F{lastRow})/H17/H18", result.FillFactor);
        editor.Formula("H21", $"INDEX(E3:E{lastRow},MATCH(MAX(F3:F{lastRow}),F3:F{lastRow},0))", result.MaximumPowerCurrentDensity);
        editor.Formula("H22", $"INDEX(A3:A{lastRow},MATCH(MAX(F3:F{lastRow}),F3:F{lastRow},0))", result.MaximumPowerVoltage);
    }

    private static DataWindow? WriteDark(SheetEditor editor, IReadOnlyList<Measurement>? dark,
        CalculationResult result, List<string> warnings)
    {
        if (dark is null || dark.Count < 2)
        {
            foreach (var cell in new[] { "H7", "H10", "H11", "H23", "H24", "H25" })
                editor.Missing(cell);
            if (dark is not null)
                warnings.Add("Для расчета темновой ВАХ недостаточно точек.");
            return null;
        }

        var voltage = dark.Select(measurement => measurement.Voltage).ToArray();
        var density = dark.Select(measurement => measurement.Current / result.Area).ToArray();
        var w = density.Select(value => result.ShortCircuitCurrentDensity - value).ToArray();
        var x = w.Select(value => value - result.MaximumPowerCurrentDensity).ToArray();
        for (var index = 0; index < dark.Count; index++)
        {
            var row = index + 3;
            editor.Number(row, 19, dark[index].Voltage);
            editor.Number(row, 20, dark[index].Current);
        }
        if (density.Concat(w).Concat(x).Any(value => !double.IsFinite(value)))
        {
            foreach (var cell in new[] { "H7", "H10", "H11", "H23", "H24", "H25" })
                editor.Missing(cell);
            warnings.Add("Данные темновой ВАХ выходят за допустимый числовой диапазон; соответствующие параметры недоступны.");
            return null;
        }
        for (var index = 0; index < dark.Count; index++)
        {
            var row = index + 3;
            editor.Formula(row, 21, $"U{row}/$V$2", density[index]);
            editor.Formula(row, 22, $"$H$5-V{row}", w[index]);
            editor.Formula(row, 23, $"$H$5-$H$21-V{row}", x[index]);
        }

        var uDark = WriteIntercept(editor, "H7", "T", "W", w, voltage, 2, warnings, "темновой ВАХ около W=0");
        WriteIntercept(editor, "H10", "W", "X", x, w, 3, warnings, "темновой ВАХ около X=0");
        var uDarkMpp = WriteIntercept(editor, "H11", "T", "X", x, voltage, 3, warnings, "темновой ВАХ около X=0");
        double? shunt = uDark.HasValue ? (uDark.Value - result.OpenCircuitVoltage) / result.ShortCircuitCurrentDensity : null;
        double? series = uDarkMpp.HasValue ? (uDarkMpp.Value - result.MaximumPowerVoltage) / result.MaximumPowerCurrentDensity : null;
        editor.FormulaOrMissing("H23", "(H7-H6)/H5", shunt);
        editor.FormulaOrMissing("H24", "(H11-H22)/H21", series);
        editor.FormulaOrMissing("H25", "(H11-H22-(H5-H21)*H23)/H21", shunt.HasValue && uDarkMpp.HasValue
            ? (uDarkMpp.Value - result.MaximumPowerVoltage -
               (result.ShortCircuitCurrentDensity - result.MaximumPowerCurrentDensity) * shunt.Value) / result.MaximumPowerCurrentDensity
            : null);
        try
        {
            return CurveMath.ZeroWindow(w, 10);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static void WriteSinton(SheetEditor editor, IReadOnlyList<Measurement>? sinton,
        CalculationResult result, List<string> warnings)
    {
        foreach (var cell in new[] { "H8", "H9", "H20", "H26" })
            editor.Missing(cell);
        if (sinton is null || sinton.Count < 2)
            return;

        var k = sinton.Select(measurement => measurement.Voltage).ToArray();
        var m = sinton.Select(measurement => -measurement.Current).ToArray();
        var n = k.Select(value => value - result.OpenCircuitVoltage).ToArray();
        for (var index = 0; index < sinton.Count; index++)
        {
            var row = index + 3;
            editor.Number(row, 10, k[index]);
            editor.Number(row, 11, sinton[index].Current);
            editor.Formula(row, 12, $"-L{row}", m[index]);
            editor.Formula(row, 13, $"K{row}-$H$6", n[index]);
            editor.Formula(row, 14, $"K{row}", k[index]);
        }

        var reference = WriteIntercept(editor, "H8", "M", "N", n, m, 2, warnings, "данных Синтон около N=0");
        if (!reference.HasValue || reference.Value == 0)
        {
            editor.Missing("H8");
            if (reference == 0)
                warnings.Add("В данных Синтон опорный ток равен нулю; расчет недоступен.");
            return;
        }
        var p = m.Select(value => (reference.Value - value) / reference.Value * result.ShortCircuitCurrentDensity).ToArray();
        var q = p.Select(value => value - result.MaximumPowerCurrentDensity).ToArray();
        var power = p.Select((value, index) => value * k[index]).ToArray();
        if (p.Concat(q).Concat(power).Any(value => !double.IsFinite(value)))
        {
            warnings.Add("Данные Синтон выходят за допустимый числовой диапазон; соответствующие параметры недоступны.");
            return;
        }
        for (var index = 0; index < sinton.Count; index++)
        {
            var row = index + 3;
            editor.Formula(row, 15, $"($H$8-M{row})/$H$8*$H$5", p[index]);
            editor.Formula(row, 16, $"P{row}-$H$21", q[index]);
            editor.Formula(row, 17, $"O{row}*P{row}", power[index]);
        }
        var uMpp = WriteIntercept(editor, "H9", "K", "Q", q, k, 2, warnings, "данных Синтон около Q=0");
        editor.Formula("H20", $"MAX(R3:R{sinton.Count + 2})/H17/H18",
            power.Max() / result.OpenCircuitVoltage / result.ShortCircuitCurrentDensity);
        editor.FormulaOrMissing("H26", "(H9-H22)/H21", uMpp.HasValue
            ? (uMpp.Value - result.MaximumPowerVoltage) / result.MaximumPowerCurrentDensity
            : null);
    }

    private static double? WriteIntercept(SheetEditor editor, string address, string yColumn, string xColumn,
        IReadOnlyList<double> x, IReadOnlyList<double> y, int count, List<string> warnings, string description)
    {
        try
        {
            var window = CurveMath.ZeroWindow(x, count);
            var value = CurveMath.Intercept(x, y, window);
            if (!double.IsFinite(value))
                throw new InvalidDataException("Результат регрессии не является конечным числом.");
            editor.Formula(address, $"INTERCEPT({yColumn}{window.FirstExcelRow}:{yColumn}{window.LastExcelRow},{xColumn}{window.FirstExcelRow}:{xColumn}{window.LastExcelRow})", value);
            return value;
        }
        catch (InvalidDataException)
        {
            editor.Missing(address);
            var warning = $"Для {description} нет подходящего перехода через ноль; часть параметров недоступна.";
            if (!warnings.Contains(warning))
                warnings.Add(warning);
            return null;
        }
    }

    private static IReadOnlyList<Measurement> ReadMain(string path)
    {
        try
        {
            var measurements = MeasurementReader.Read(path, true);
            if (measurements.Count > MaximumMeasurements)
                throw new InvalidDataException($"Формат XLS допускает не более {MaximumMeasurements} измерений.");
            return measurements;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("Не удалось прочитать световую ВАХ. " + exception.Message, exception);
        }
    }

    private static IReadOnlyList<Measurement>? ReadOptional(string? path, string description, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            warnings.Add($"Файл {description} не выбран; соответствующие параметры недоступны.");
            return null;
        }
        try
        {
            var measurements = MeasurementReader.Read(path, false);
            if (measurements.Count is < 2 or > MaximumMeasurements)
                throw new InvalidDataException("Недопустимое число измерений.");
            return measurements;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            warnings.Add($"Не удалось прочитать файл {description}: {exception.Message} Соответствующие параметры недоступны.");
            return null;
        }
    }

    private static IReadOnlyList<Measurement> ReadTemplateDark(HSSFSheet sheet)
    {
        var measurements = new List<Measurement>();
        for (var row = 2; row <= sheet.LastRowNum; row++)
        {
            var voltage = NumericValue(sheet.GetRow(row)?.GetCell(19));
            var current = NumericValue(sheet.GetRow(row)?.GetCell(20));
            if (voltage.HasValue && current.HasValue)
                measurements.Add(new Measurement(voltage.Value, current.Value));
        }
        return measurements;
    }

    private static ManagedWorkbook LoadTemplate(string? path)
    {
        try
        {
            using var stream = string.IsNullOrWhiteSpace(path)
                ? typeof(XlsReportWriter).Assembly.GetManifestResourceStream("SolarReport.Template.xls")
                : File.OpenRead(path);
            if (stream is null)
                throw new InvalidDataException("Встроенный шаблон XLS не найден.");
            return new ManagedWorkbook(stream);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException("Не удалось прочитать шаблон XLS. Проверьте выбранный файл.", exception);
        }
    }

    private static string ValidateOutputPath(ReportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OutputPath) ||
            !string.Equals(Path.GetExtension(request.OutputPath), ".xls", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Укажите файл результата с расширением .xls.", nameof(request));
        var output = Path.GetFullPath(request.OutputPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var path in new[] { request.InputPath, request.TemplatePath, request.DarkInputPath, request.SintonInputPath })
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var source = Path.GetFullPath(path);
            if (string.Equals(output, source, comparison) ||
                string.Equals(ResolvePath(output), ResolvePath(source), comparison))
                throw new ArgumentException("Файл результата не должен совпадать с исходным файлом или шаблоном.", nameof(request));
        }
        return output;
    }

    private static string ResolvePath(string path, int depth = 0)
    {
        if (depth > 64)
            throw new ArgumentException("Слишком много связанных путей при проверке исходных файлов.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (var component in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, component);
            FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            if (info.LinkTarget is null)
            {
                current = candidate;
                continue;
            }
            var target = info.ResolveLinkTarget(true)
                ?? throw new ArgumentException("Не удалось проверить связанный путь исходного файла.", nameof(path));
            current = ResolvePath(target.FullName, depth + 1);
        }
        return current;
    }

    private static void SaveAtomically(HSSFWorkbook workbook, string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        var temporaryPath = Path.Combine(outputDirectory, ".solar-report-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                workbook.Write(stream);
                stream.Flush(true);
            }
            File.Move(temporaryPath, outputPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Не удалось сохранить отчет XLS. Проверьте путь и доступ к файлу.", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static double? NumericValue(ICell? cell)
    {
        if (cell is null || cell.CellType != CellType.Numeric &&
            (cell.CellType != CellType.Formula || cell.CachedFormulaResultType != CellType.Numeric))
            return null;
        var value = cell.NumericCellValue;
        return double.IsFinite(value) ? value : null;
    }

    private static int CountZeroCrossings(IEnumerable<double> values)
    {
        var crossings = 0;
        var previousSign = 0;
        var inZeroRun = false;
        foreach (var value in values)
        {
            var sign = Math.Sign(value);
            if (sign == 0)
            {
                if (!inZeroRun)
                    crossings++;
                inZeroRun = true;
            }
            else
            {
                if (previousSign != 0 && previousSign != sign && !inZeroRun)
                    crossings++;
                previousSign = sign;
                inZeroRun = false;
            }
        }
        return crossings;
    }

    private sealed class ManagedWorkbook(Stream stream) : HSSFWorkbook(stream), IDisposable
    {
        public void Dispose() => Close();
    }

    private static void UpdateChartRanges(HSSFWorkbook workbook, HSSFSheet sheet, int mainCount,
        int darkCount, int sintonCount, DataWindow? darkDetail, bool keepTemplateDark)
    {
        var evaluation = HSSFEvaluationWorkbook.Create(workbook);
        var charts = HSSFChart.GetSheetCharts(sheet);
        for (var chartIndex = 0; chartIndex < charts.Length; chartIndex++)
        {
            var chart = charts[chartIndex];
            var logarithmic = ChartAxes(sheet, chartIndex).Any(axis => axis.IsLogarithmicScale);
            foreach (var series in chart.Series)
            {
                var valuesLink = series.GetDataValues();
                var categoriesLink = series.GetDataCategoryLabels();
                if (!OwnedArea(valuesLink, evaluation, out var values) ||
                    !OwnedArea(categoriesLink, evaluation, out var categories))
                    continue;
                var count = values.FirstColumn switch
                {
                    >= 0 and <= 5 => mainCount,
                    >= 10 and <= 17 => sintonCount,
                    >= 19 and <= 23 => darkCount,
                    _ => 0
                };
                if (count == 0)
                    continue;
                var firstRow = 2;
                var lastRow = count + 1;
                if (values.FirstColumn is >= 19 and <= 23 && values.LastRow - values.FirstRow < 10 && darkDetail is not null)
                {
                    firstRow = darkDetail.StartIndex + 2;
                    lastRow = firstRow + darkDetail.Count - 1;
                }
                else if (values.FirstColumn is >= 19 and <= 23 && logarithmic)
                {
                    if (keepTemplateDark)
                    {
                        firstRow = values.FirstRow;
                        lastRow = Math.Min(values.LastRow, darkCount + 1);
                    }
                    else if (PositiveDarkWindow(sheet, darkCount, categories.FirstColumn, values.FirstColumn) is { } window)
                    {
                        firstRow = window.StartIndex + 2;
                        lastRow = firstRow + window.Count - 1;
                    }
                }
                series.SetValuesCellRange(new CellRangeAddress(firstRow, lastRow, values.FirstColumn, values.LastColumn));
                series.SetCategoryLabelsCellRange(new CellRangeAddress(firstRow, lastRow, categories.FirstColumn, categories.LastColumn));
            }
        }
    }

    private static DataWindow? PositiveDarkWindow(HSSFSheet sheet, int count, int xColumn, int yColumn)
    {
        var bestStart = 0;
        var bestCount = 0;
        var start = 0;
        var length = 0;
        for (var index = 0; index < count; index++)
        {
            if (NumericValue(sheet.GetRow(index + 2)?.GetCell(xColumn)) > 0 &&
                NumericValue(sheet.GetRow(index + 2)?.GetCell(yColumn)) > 0)
            {
                if (length == 0)
                    start = index;
                length++;
                if (length > bestCount)
                {
                    bestStart = start;
                    bestCount = length;
                }
            }
            else
                length = 0;
        }
        return bestCount > 0 ? new DataWindow(bestStart, bestCount) : null;
    }

    private static List<ValueRangeRecord> ChartAxes(HSSFSheet sheet, int chartIndex)
    {
        var axes = new List<ValueRangeRecord>();
        var currentChart = -1;
        foreach (var record in sheet.Sheet.Records)
        {
            if (record is BOFRecord bof && bof.Type == BOFRecordType.Chart)
                currentChart++;
            if (currentChart == chartIndex && record is ValueRangeRecord axis)
                axes.Add(axis);
        }
        return axes;
    }

    private static void FitMainChartAxes(HSSFSheet sheet, CalculationResult result)
    {
        var voltages = new List<double> { result.OpenCircuitVoltage, result.MaximumPowerVoltage };
        foreach (var row in new[] { 6, 8, 10 })
            if (NumericValue(sheet.GetRow(row)?.GetCell(7)) is { } voltage)
                voltages.Add(voltage);
        var voltageMaximum = voltages.Max();
        var span = Math.Max(Math.Abs(result.ShortCircuitCurrentDensity - result.MaximumPowerCurrentDensity),
            0.02 * result.ShortCircuitCurrentDensity);
        foreach (var chartIndex in new[] { 1, 3 })
        {
            var axes = ChartAxes(sheet, chartIndex);
            if (axes.Count < 2)
                continue;
            SetAxisBounds(axes[0], -0.05 * result.OpenCircuitVoltage, 1.05 * voltageMaximum);
            if (chartIndex == 1)
                SetAxisBounds(axes[1], Math.Max(0,
                    Math.Min(result.ShortCircuitCurrentDensity, result.MaximumPowerCurrentDensity) - 0.1 * span),
                    Math.Max(result.ShortCircuitCurrentDensity, result.MaximumPowerCurrentDensity) + 0.1 * span);
            else
                SetAxisBounds(axes[1], 0,
                    1.05 * Math.Max(result.ShortCircuitCurrentDensity, result.MaximumPowerCurrentDensity));
        }
    }

    private static void SetAxisBounds(ValueRangeRecord axis, double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
            return;
        axis.MinimumAxisValue = minimum;
        axis.MaximumAxisValue = maximum;
        axis.IsAutomaticMinimum = false;
        axis.IsAutomaticMaximum = false;
        axis.IsAutomaticMajor = true;
        axis.IsAutomaticMinor = true;
    }

    private static void FormatMainChartAxes(HSSFWorkbook workbook, HSSFSheet sheet)
    {
        var dataFormat = workbook.CreateDataFormat();
        var records = sheet.Sheet.Records;
        var chartIndex = -1;
        short axisType = -1;
        for (var index = 0; index < records.Count; index++)
        {
            if (records[index] is BOFRecord bof && bof.Type == BOFRecordType.Chart)
            {
                chartIndex++;
                axisType = -1;
            }
            if (records[index] is AxisRecord axis)
                axisType = axis.AxisType;
            if (chartIndex is not (1 or 3) || axisType is not (0 or 1) || records[index] is not ValueRangeRecord range)
                continue;
            var existingIndex = -1;
            for (var next = index + 1; next < records.Count && records[next] is not (EndRecord or AxisRecord or EOFRecord); next++)
                if (records[next] is NPOI.HSSF.Record.Record format && format.Sid == NumberFormatIndexRecord.sid)
                {
                    existingIndex = next;
                    break;
                }
            var magnitude = Math.Max(Math.Abs(range.MinimumAxisValue), Math.Abs(range.MaximumAxisValue));
            var standardFormat = axisType == 0 ? "0.00" : "0.00000";
            var minimum = axisType == 0 ? 0.01 : 0.0001;
            var formatIndex = dataFormat.GetFormat(magnitude < minimum || magnitude > 1000 ? "0.00E+00" : standardFormat);
            if (existingIndex >= 0)
                records[existingIndex] = new NumberFormatIndexRecord { FormatIndex = formatIndex };
            else
                records.Insert(index + 1, new NumberFormatIndexRecord { FormatIndex = formatIndex });
        }
    }

    private static bool OwnedArea(LinkedDataRecord? link, HSSFEvaluationWorkbook evaluation, out AreaPtgBase area)
    {
        area = null!;
        if (link?.FormulaOfLink is not { Length: 1 } tokens || tokens[0] is not AreaPtgBase candidate ||
            candidate.FirstColumn != candidate.LastColumn || !OwnedReference(candidate, evaluation))
            return false;
        area = candidate;
        return true;
    }

    private static bool OwnedReference(Ptg token, HSSFEvaluationWorkbook evaluation)
    {
        var externalIndex = token switch
        {
            Area3DPtg area => area.ExternSheetIndex,
            Ref3DPtg reference => reference.ExternSheetIndex,
            AreaPtgBase or RefPtgBase => -1,
            _ => int.MinValue
        };
        return externalIndex == -1 || externalIndex >= 0 &&
            evaluation.ConvertFromExternSheetIndex(externalIndex) == evaluation.GetSheetIndex(SheetName) &&
            evaluation.GetSheetFirstNameByExternSheet(externalIndex) == SheetName &&
            evaluation.GetSheetLastNameByExternSheet(externalIndex) == SheetName;
    }

    private static void UpdateChartCaches(HSSFWorkbook workbook, HSSFSheet sheet)
    {
        var charts = HSSFChart.GetSheetCharts(sheet);
        var evaluation = HSSFEvaluationWorkbook.Create(workbook);
        var records = sheet.Sheet.Records;
        var blocks = new List<(int Start, int End)>();
        var start = -1;
        for (var index = 0; index < records.Count; index++)
        {
            if (records[index] is BOFRecord bof && bof.Type == BOFRecordType.Chart)
                start = index;
            else if (records[index] is EOFRecord && start >= 0)
            {
                blocks.Add((start, index));
                start = -1;
            }
        }
        for (var chartIndex = blocks.Count - 1; chartIndex >= 0; chartIndex--)
        {
            var chart = charts[chartIndex];
            var block = blocks[chartIndex];
            var axes = ChartAxes(sheet, chartIndex);
            var points = new Dictionary<int, List<(int Row, double X, double Y)>>();
            for (var seriesIndex = 0; seriesIndex < chart.Series.Length; seriesIndex++)
            {
                var series = chart.Series[seriesIndex];
                var x = ReadChartValues(sheet, series.GetDataCategoryLabels(), evaluation);
                var y = ReadChartValues(sheet, series.GetDataValues(), evaluation);
                if (x is null || y is null)
                    continue;
                var pairs = new List<(int Row, double X, double Y)>();
                for (var row = 0; row < Math.Min(x.Count, y.Count); row++)
                    if (x[row].HasValue && y[row].HasValue &&
                        (!axes.Any(axis => axis.IsLogarithmicScale) || x[row] > 0 && y[row] > 0))
                        pairs.Add((row, x[row]!.Value, y[row]!.Value));
                points[seriesIndex] = pairs;
                var seriesRecord = series.GetSeries();
                seriesRecord.NumCategories = unchecked((short)(pairs.Count == 0 ? 0 : x.Count));
                seriesRecord.NumValues = unchecked((short)(pairs.Count == 0 ? 0 : y.Count));
                seriesRecord.CategoryDataType = 1;
                seriesRecord.ValuesDataType = 1;
            }
            if (points.Count == 0)
                continue;
            foreach (var axis in axes)
            {
                axis.IsAutomaticMinimum = true;
                axis.IsAutomaticMaximum = true;
                axis.IsAutomaticMajor = true;
                axis.IsAutomaticMinor = true;
            }
            for (var index = block.End - 1; index > block.Start; index--)
            {
                if (records[index] is not SeriesIndexRecord cache || cache.Index is not (1 or 2))
                    continue;
                var end = index + 1;
                while (end < records.Count && records[end] is CellValueRecordInterface)
                    end++;
                var replacements = new List<RecordBase>();
                for (var original = index + 1; original < end; original++)
                    if (records[original] is CellValueRecordInterface cell && !points.ContainsKey(cell.Column))
                        replacements.Add(records[original]);
                foreach (var entry in points)
                    foreach (var point in entry.Value)
                        replacements.Add(new NumberRecord
                        {
                            Row = point.Row,
                            Column = entry.Key,
                            XFIndex = 0,
                            Value = cache.Index == 2 ? point.X : point.Y
                        });
                replacements.Sort((left, right) =>
                {
                    var a = (CellValueRecordInterface)left;
                    var b = (CellValueRecordInterface)right;
                    var rowOrder = a.Row.CompareTo(b.Row);
                    return rowOrder != 0 ? rowOrder : a.Column.CompareTo(b.Column);
                });
                records.RemoveRange(index + 1, end - index - 1);
                records.InsertRange(index + 1, replacements);
            }
            var chartEnd = block.Start + 1;
            while (chartEnd < records.Count && records[chartEnd] is not EOFRecord)
                chartEnd++;
            for (var index = block.Start + 1; index < chartEnd; index++)
                if (records[index] is DimensionsRecord dimensions)
                {
                    dimensions.FirstRow = 0;
                    dimensions.LastRow = chart.Series.Max(series => Math.Max(
                        (ushort)series.GetSeries().NumCategories, (ushort)series.GetSeries().NumValues));
                    dimensions.FirstCol = 0;
                    dimensions.LastCol = chart.Series.Length;
                }
        }
    }

    private static IReadOnlyList<double?>? ReadChartValues(HSSFSheet sheet, LinkedDataRecord? link,
        HSSFEvaluationWorkbook evaluation)
    {
        if (link?.FormulaOfLink is not { Length: 1 } tokens || !OwnedReference(tokens[0], evaluation))
            return null;
        if (tokens[0] is RefPtgBase reference)
            return new[] { NumericValue(sheet.GetRow(reference.Row)?.GetCell(reference.Column)) };
        if (tokens[0] is not AreaPtgBase area || area.FirstColumn != area.LastColumn)
            return null;
        var values = new List<double?>();
        for (var row = area.FirstRow; row <= area.LastRow; row++)
            values.Add(NumericValue(sheet.GetRow(row)?.GetCell(area.FirstColumn)));
        return values;
    }

    private sealed class SheetEditor
    {
        private readonly HSSFSheet sheet;
        private readonly ICellStyle?[] styles = new ICellStyle?[24];
        private readonly List<ICell> formulas = [];

        public SheetEditor(HSSFSheet sheet)
        {
            this.sheet = sheet;
            for (var column = 0; column < styles.Length; column++)
                styles[column] = sheet.GetRow(2)?.GetCell(column)?.CellStyle;
        }

        public void DetachArrays()
        {
            var groups = new HashSet<string>();
            foreach (IRow row in sheet)
                foreach (ICell cell in row)
                {
                    var owns = row.RowNum >= 2 && (cell.ColumnIndex is >= 0 and <= 5 or >= 10 and <= 17 or >= 19 and <= 23) ||
                        cell.ColumnIndex == 7 && row.RowNum is >= 4 and <= 10 or >= 13 and <= 25 ||
                        cell.ColumnIndex == 8 && row.RowNum == 4;
                    if (owns && cell.IsPartOfArrayFormulaGroup && groups.Add(cell.ArrayFormulaRange.FormatAsString()))
                        sheet.RemoveArrayFormula(cell);
                }
        }

        public void ClearColumns(int first, int last)
        {
            for (var row = 2; row <= sheet.LastRowNum; row++)
                for (var column = first; column <= last; column++)
                    sheet.GetRow(row)?.GetCell(column)?.SetCellType(CellType.Blank);
        }

        public void Number(string address, double value)
        {
            var reference = new CellReference(address);
            Number(reference.Row + 1, reference.Col, value);
        }

        public void Number(int excelRow, int column, double value)
        {
            var cell = Cell(excelRow - 1, column);
            cell.SetCellType(CellType.Numeric);
            cell.SetCellValue(value);
        }

        public void Formula(string address, string formula, double value)
        {
            var reference = new CellReference(address);
            Formula(reference.Row + 1, reference.Col, formula, value);
        }

        public void Formula(int excelRow, int column, string formula, double value)
        {
            var cell = Cell(excelRow - 1, column);
            cell.SetCellFormula(formula);
            cell.SetCellValue(value);
            formulas.Add(cell);
        }

        public void FormulaOrMissing(string address, string formula, double? value)
        {
            if (value.HasValue && double.IsFinite(value.Value))
                Formula(address, formula, value.Value);
            else
                Missing(address);
        }

        public void Missing(string address)
        {
            var reference = new CellReference(address);
            var cell = Cell(reference.Row, reference.Col);
            cell.SetCellType(CellType.String);
            cell.SetCellValue(MissingData);
        }

        public void EvaluateFormulas(HSSFWorkbook workbook)
        {
            var evaluator = new HSSFFormulaEvaluator(workbook);
            foreach (var cell in formulas)
            {
                if (cell.CellType != CellType.Formula)
                    continue;
                evaluator.EvaluateFormulaCell(cell);
                if (cell.CachedFormulaResultType == CellType.Error ||
                    cell.CachedFormulaResultType == CellType.Numeric && !double.IsFinite(cell.NumericCellValue))
                    throw new InvalidDataException($"Не удалось вычислить формулу в ячейке {new CellReference(cell.RowIndex, cell.ColumnIndex).FormatAsString()}.");
            }
        }

        private ICell Cell(int rowIndex, int column)
        {
            var row = sheet.GetRow(rowIndex);
            if (row is null)
            {
                row = sheet.CreateRow(rowIndex);
                row.Height = sheet.GetRow(2)?.Height ?? sheet.DefaultRowHeight;
            }
            var cell = row.GetCell(column);
            if (cell is null)
            {
                cell = row.CreateCell(column);
                if (column < styles.Length && styles[column] is not null)
                    cell.CellStyle = styles[column];
            }
            return cell;
        }
    }
}
