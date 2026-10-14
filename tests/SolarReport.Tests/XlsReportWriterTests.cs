using System.Globalization;
using NPOI.HSSF.UserModel;
using NPOI.HSSF.Record;
using NPOI.HSSF.Record.Chart;
using NPOI.SS.Formula.PTG;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using SolarReport.Core;
using Xunit;

namespace SolarReport.Tests;

public sealed class XlsReportWriterTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "solar-xls-tests-" + Guid.NewGuid().ToString("N"));

    public XlsReportWriterTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void PreservesTemplateSheetsChartsAndFormatting()
    {
        var input = WriteCurve("main.txt", 199);
        var output = Path.Combine(directory, "report.xls");
        using var template = LoadTemplate();

        var result = XlsReportWriter.Generate(new ReportRequest(input, output, 0.7));

        using var workbook = Load(output);
        Assert.Equal(template.NumberOfSheets, workbook.NumberOfSheets);
        for (var index = 0; index < template.NumberOfSheets; index++)
        {
            Assert.Equal(template.GetSheetName(index), workbook.GetSheetName(index));
            var before = (HSSFSheet)template.GetSheetAt(index);
            var after = (HSSFSheet)workbook.GetSheetAt(index);
            Assert.Equal(ChartGeometry(before), ChartGeometry(after));
            Assert.Equal(ChartMarkers(before), ChartMarkers(after));
            Assert.Equal(before.NumMergedRegions, after.NumMergedRegions);
            for (var column = 0; column < 25; column++)
                Assert.Equal(before.GetColumnWidth(column), after.GetColumnWidth(column));
            foreach (IRow row in before)
                foreach (ICell cell in row)
                    Assert.Equal(cell.CellStyle.Index, after.GetRow(cell.RowIndex)?.GetCell(cell.ColumnIndex)?.CellStyle.Index);
            if (before.SheetName != "С=0.7")
                Assert.Equal(Snapshot(before), Snapshot(after));
        }
        Assert.Equal(4, HSSFChart.GetSheetCharts((HSSFSheet)workbook.GetSheet("С=0.7")).Length);
        Assert.Equal(199, result.MeasurementCount);
        Assert.Equal(0.7, Value(workbook, "H1"));
        Assert.Equal(result.Calculations.ShortCircuitCurrentDensity, Value(workbook, "H5"), 12);
        Assert.Equal(result.Calculations.OpenCircuitVoltage, Value(workbook, "H6"), 12);
        Assert.Equal(result.Calculations.MaximumPowerVoltage, Value(workbook, "H22"), 12);
        Assert.Equal(result.Calculations.MaximumPowerCurrentDensity, Value(workbook, "H21"), 12);
        Assert.Equal("AVERAGE(E" + result.Calculations.ShortCircuitWindow.FirstExcelRow + ":E" +
            result.Calculations.ShortCircuitWindow.LastExcelRow + ")", Cell(workbook, "H5").CellFormula);
        Assert.Equal("INTERCEPT(D" + result.Calculations.OpenCircuitWindow.FirstExcelRow + ":D" +
            result.Calculations.OpenCircuitWindow.LastExcelRow + ",E" + result.Calculations.OpenCircuitWindow.FirstExcelRow + ":E" +
            result.Calculations.OpenCircuitWindow.LastExcelRow + ")", Cell(workbook, "H6").CellFormula);
        AssertNoErrors(workbook.GetSheet("С=0.7"));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(321)]
    public void WritesEveryMeasurementClearsOldRowsAndUpdatesMainChartRanges(int count)
    {
        var input = WriteCurve("main.txt", count);
        var output = Path.Combine(directory, "report.xls");
        var measurements = MeasurementReader.Read(input);

        var result = XlsReportWriter.Generate(new ReportRequest(input, output, 0.7));

        using var workbook = Load(output);
        var sheet = (HSSFSheet)workbook.GetSheet("С=0.7");
        for (var i = 0; i < count; i++)
        {
            var excelRow = i + 3;
            Assert.Equal(measurements[i].Voltage, Value(workbook, "A" + excelRow), 12);
            Assert.Equal(measurements[i].Current, Value(workbook, "B" + excelRow), 12);
            Assert.Equal(measurements[i].Time!.Value, Value(workbook, "C" + excelRow), 12);
            Assert.Equal(measurements[i].Voltage, Value(workbook, "D" + excelRow), 12);
            Assert.Equal(-measurements[i].Current / 0.7, Value(workbook, "E" + excelRow), 12);
            Assert.InRange(Math.Abs(measurements[i].Voltage * -measurements[i].Current / 0.7 - Value(workbook, "F" + excelRow)), 0, 1e-12);
            Assert.Equal("A" + excelRow, Cell(workbook, "D" + excelRow).CellFormula);
        }
        if (count < 199)
            for (var row = count + 2; row <= 200; row++)
                for (var column = 0; column < 6; column++)
                    Assert.Equal(CellType.Blank, sheet.GetRow(row)?.GetCell(column)?.CellType ?? CellType.Blank);
        Assert.Equal("MAX(F3:F" + (count + 2) + ")*10/H14", Cell(workbook, "H16").CellFormula);
        Assert.Equal(count, result.MeasurementCount);
        var mainSeries = HSSFChart.GetSheetCharts(sheet).SelectMany(chart => chart.Series)
            .Where(series => IsMainRange(series.GetValuesCellRange())).ToArray();
        Assert.NotEmpty(mainSeries);
        foreach (var series in mainSeries)
        {
            Assert.Equal(2, series.GetValuesCellRange().FirstRow);
            Assert.Equal(count + 1, series.GetValuesCellRange().LastRow);
            Assert.Equal(2, series.GetCategoryLabelsCellRange().FirstRow);
            Assert.Equal(count + 1, series.GetCategoryLabelsCellRange().LastRow);
        }
        var plottedCurrent = ChartCache(sheet, 1, 1, 1);
        var plottedVoltage = ChartCache(sheet, 1, 2, 1);
        Assert.Equal(count, plottedCurrent.Length);
        Assert.Equal(count, plottedVoltage.Length);
        Assert.Equal(measurements[^1].Voltage, plottedVoltage[^1], 12);
        Assert.Equal(-measurements[^1].Current / 0.7, plottedCurrent[^1], 12);
        Assert.Equal(new[] { result.Calculations.MaximumPowerVoltage }, ChartCache(sheet, 1, 2, 3));
        Assert.Equal(new[] { result.Calculations.MaximumPowerCurrentDensity }, ChartCache(sheet, 1, 1, 3));
        AssertNoErrors(sheet);
    }

    [Fact]
    public void MissingSintonDoesNotReuseTemplateMeasurementsOrProduceFakeResults()
    {
        var output = Path.Combine(directory, "report.xls");
        var result = XlsReportWriter.Generate(new ReportRequest(WriteCurve("main.txt", 40), output, 0.7));

        using var workbook = Load(output);
        foreach (var address in new[] { "H8", "H9", "H20", "H26" })
            Assert.Equal("нет данных", Cell(workbook, address).StringCellValue);
        for (var row = 2; row <= 501; row++)
            for (var column = 10; column <= 17; column++)
                Assert.Equal(CellType.Blank, workbook.GetSheet("С=0.7").GetRow(row)?.GetCell(column)?.CellType ?? CellType.Blank);
        Assert.Contains(result.Warnings, warning => warning.Contains("Синтон", StringComparison.OrdinalIgnoreCase));
        foreach (var chart in new[] { 1, 3 })
            foreach (var series in new[] { 2, 4 })
            {
                Assert.Empty(ChartCache((HSSFSheet)workbook.GetSheet("С=0.7"), chart, 1, series));
                Assert.Empty(ChartCache((HSSFSheet)workbook.GetSheet("С=0.7"), chart, 2, series));
                var missingSeries = HSSFChart.GetSheetCharts((HSSFSheet)workbook.GetSheet("С=0.7"))[chart].Series[series];
                Assert.Equal((short)0, missingSeries.GetSeries().NumValues);
                Assert.Equal((short)0, missingSeries.GetSeries().NumCategories);
            }
        AssertNoErrors(workbook.GetSheet("С=0.7"));
    }

    [Fact]
    public void AreaChangeRecalculatesDarkDensityFromTheSameArea()
    {
        var input = WriteCurve("main.txt", 40);
        var dark = WriteDark("dark.txt", 100);
        var output = Path.Combine(directory, "report.xls");

        XlsReportWriter.Generate(new ReportRequest(input, output, 1.2, DarkInputPath: dark));

        using var workbook = Load(output);
        Assert.Equal(1.2, Value(workbook, "V2"));
        Assert.Equal(MeasurementReader.Read(dark, false)[50].Current / 1.2, Value(workbook, "V53"), 12);
        Assert.Equal("U53/$V$2", Cell(workbook, "V53").CellFormula);
        Assert.Equal(CellType.Blank, Cell(workbook, "T103").CellType);
        Assert.Equal(CellType.Blank, Cell(workbook, "X103").CellType);
        Assert.Equal(CellType.Formula, Cell(workbook, "H7").CellType);
        AssertNoErrors(workbook.GetSheet("С=0.7"));
    }

    [Fact]
    public void UsesTemplateReferenceDensityUntilAnExplicitOverrideIsSupplied()
    {
        var templatePath = Path.Combine(directory, "custom.xls");
        using (var template = LoadTemplate())
        {
            Cell(template, "H4").SetCellValue(0.041);
            using var stream = File.Create(templatePath);
            template.Write(stream);
        }
        var input = WriteCurve("main.txt", 40);
        var output = Path.Combine(directory, "report.xls");

        var result = XlsReportWriter.Generate(new ReportRequest(input, output, 0.7, TemplatePath: templatePath));

        using (var workbook = Load(output))
            Assert.Equal(0.041, Value(workbook, "H4"));
        Assert.Equal(0.041, result.Calculations.ReferenceCurrentDensity);
        XlsReportWriter.Generate(new ReportRequest(input, output, 0.7, TemplatePath: templatePath, ReferenceCurrentDensity: 0.035));
        using var overridden = Load(output);
        Assert.Equal(0.035, Value(overridden, "H4"));
    }

    [Fact]
    public void InvalidOptionalFileIsReportedWithoutPreventingMainExport()
    {
        var invalid = Path.Combine(directory, "invalid.txt");
        File.WriteAllText(invalid, "0\tbad\n1\t0");
        var output = Path.Combine(directory, "report.xls");

        var result = XlsReportWriter.Generate(new ReportRequest(WriteCurve("main.txt", 40), output, 0.7,
            SintonInputPath: invalid));

        using var workbook = Load(output);
        Assert.Equal("нет данных", Cell(workbook, "H8").StringCellValue);
        Assert.True(double.IsFinite(Value(workbook, "H6")));
        Assert.Contains(result.Warnings, warning => warning.Contains("Синтон", StringComparison.OrdinalIgnoreCase));
        AssertNoErrors(workbook.GetSheet("С=0.7"));
    }

    [Fact]
    public void DoesNotRemapChartSeriesThatReferencesAnotherWorksheet()
    {
        var templatePath = Path.Combine(directory, "custom.xls");
        using (var template = LoadTemplate())
        {
            var sheet = (HSSFSheet)template.GetSheet("С=0.7");
            var series = HSSFChart.GetSheetCharts(sheet).SelectMany(chart => chart.Series)
                .First(item => IsMainRange(item.GetValuesCellRange()));
            var externalIndex = HSSFEvaluationWorkbook.Create(template).GetExternalSheetIndex("С=0.89");
            foreach (var link in new[] { series.GetDataValues(), series.GetDataCategoryLabels() })
            {
                var tokens = link.FormulaOfLink;
                foreach (var area in tokens.OfType<Area3DPtg>())
                    area.ExternSheetIndex = externalIndex;
                link.FormulaOfLink = tokens;
            }
            using var stream = File.Create(templatePath);
            template.Write(stream);
        }
        var output = Path.Combine(directory, "report.xls");

        XlsReportWriter.Generate(new ReportRequest(WriteCurve("main.txt", 321), output, 0.7, TemplatePath: templatePath));

        using var workbook = Load(output);
        var evaluation = HSSFEvaluationWorkbook.Create(workbook);
        var untouched = HSSFChart.GetSheetCharts((HSSFSheet)workbook.GetSheet("С=0.7")).SelectMany(chart => chart.Series)
            .Single(series => series.GetDataValues().FormulaOfLink.OfType<Area3DPtg>().Any(area =>
                evaluation.GetSheetFirstNameByExternSheet(area.ExternSheetIndex) == "С=0.89"));
        Assert.Equal(200, untouched.GetValuesCellRange().LastRow);
        Assert.Equal(200, untouched.GetCategoryLabelsCellRange().LastRow);
    }

    [Fact]
    public void OptionalSintonUsesItsFullCurveAndProducesCachedResults()
    {
        var output = Path.Combine(directory, "report.xls");
        var sinton = WriteSinton("sinton.txt", 521);
        var result = XlsReportWriter.Generate(new ReportRequest(WriteCurve("main.txt", 60), output, 0.7,
            DarkInputPath: WriteDark("dark.txt", 321), SintonInputPath: sinton));

        using var workbook = Load(output);
        foreach (var address in new[] { "H8", "H9", "H20", "H26" })
        {
            Assert.Equal(CellType.Formula, Cell(workbook, address).CellType);
            Assert.True(double.IsFinite(Value(workbook, address)));
        }
        Assert.Equal(MeasurementReader.Read(sinton, false)[520].Voltage, Value(workbook, "K523"), 12);
        Assert.Equal("O523*P523", Cell(workbook, "R523").CellFormula);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("Синтон", StringComparison.OrdinalIgnoreCase));
        var series = HSSFChart.GetSheetCharts((HSSFSheet)workbook.GetSheet("С=0.7"))
            .SelectMany(chart => chart.Series).Where(item => item.GetValuesCellRange().FirstColumn == 15);
        Assert.NotEmpty(series);
        Assert.All(series, item => Assert.Equal(522, item.GetValuesCellRange().LastRow));
        AssertNoErrors(workbook.GetSheet("С=0.7"));
    }

    [Fact]
    public void DarkCurveWithoutRequiredRootLeavesMainResultsAndMarksUnavailableMetrics()
    {
        var dark = Path.Combine(directory, "dark.txt");
        File.WriteAllText(dark, string.Join("\n", Enumerable.Range(0, 20).Select(i => $"{i / 10d:R}\t0")));
        var output = Path.Combine(directory, "report.xls");

        var result = XlsReportWriter.Generate(new ReportRequest(WriteCurve("main.txt", 40), output, 0.7, DarkInputPath: dark));

        using var workbook = Load(output);
        Assert.Equal(result.Calculations.OpenCircuitVoltage, Value(workbook, "H6"), 12);
        foreach (var address in new[] { "H7", "H10", "H11", "H23", "H24", "H25" })
            Assert.Equal("нет данных", Cell(workbook, address).StringCellValue);
        Assert.Contains(result.Warnings, warning => warning.Contains("темнов", StringComparison.OrdinalIgnoreCase));
        AssertNoErrors(workbook.GetSheet("С=0.7"));
    }

    [Fact]
    public void WarnsWhenMainCurrentCurveHasSeveralZeroCrossings()
    {
        var input = Path.Combine(directory, "noisy.txt");
        File.WriteAllText(input, "-0.1\t-0.005\t0\n0.1\t-0.005\t1\n0.3\t-0.002\t2\n0.5\t0.001\t3\n0.7\t-0.001\t4\n0.9\t0.001\t5\n1.1\t0.002\t6");

        var result = XlsReportWriter.Generate(new ReportRequest(input, Path.Combine(directory, "report.xls"), 0.7));

        Assert.Single(result.Warnings, warning => warning.Contains("несколько переходов тока", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnusableOptionalNumericRangeDoesNotPreventMainExport()
    {
        var dark = Path.Combine(directory, "dark.txt");
        File.WriteAllText(dark, "0\t1.6E308\n0.5\t1.6E308\n1\t1.6E308");
        var output = Path.Combine(directory, "report.xls");

        var result = XlsReportWriter.Generate(new ReportRequest(WriteCurve("main.txt", 40), output, 0.5, DarkInputPath: dark));

        using var workbook = Load(output);
        Assert.Equal("нет данных", Cell(workbook, "H7").StringCellValue);
        Assert.Equal(result.Calculations.OpenCircuitVoltage, Value(workbook, "H6"), 12);
        Assert.Contains(result.Warnings, warning => warning.Contains("числов", StringComparison.OrdinalIgnoreCase));
        AssertNoErrors(workbook.GetSheet("С=0.7"));
    }

    [Fact]
    public void SintonOverflowLeavesDerivedResultsUnavailable()
    {
        var sinton = Path.Combine(directory, "sinton.txt");
        File.WriteAllText(sinton, "0\t-1E308\n0.88\t-1E-300\n0.92\t-1E-300");
        var output = Path.Combine(directory, "report.xls");

        var result = XlsReportWriter.Generate(new ReportRequest(WriteCurve("main.txt", 40), output, 0.7, SintonInputPath: sinton));

        using var workbook = Load(output);
        foreach (var address in new[] { "H9", "H20", "H26" })
            Assert.Equal("нет данных", Cell(workbook, address).StringCellValue);
        Assert.Contains(result.Warnings, warning => warning.Contains("числов", StringComparison.OrdinalIgnoreCase));
        AssertNoErrors(workbook.GetSheet("С=0.7"));
    }

    [Fact]
    public void WarnsAboutNegativeCorrectedResistanceEvenWhenOtherDarkResistancesArePositive()
    {
        var input = Path.Combine(directory, "main.txt");
        File.WriteAllLines(input, Enumerable.Range(0, 121).Select(index =>
        {
            var voltage = -0.1 + index * 0.01;
            var density = voltage <= 0.8 ? 0.005 - 0.000625 * voltage : 0.0045 * (0.9 - voltage) / 0.1;
            return FormattableString.Invariant($"{voltage:R}\t{-density:R}\t{index}");
        }));
        var dark = Path.Combine(directory, "dark.txt");
        File.WriteAllLines(dark, Enumerable.Range(0, 601).Select(index =>
            FormattableString.Invariant($"{index * 0.2:R}\t{0.00005 * index * 0.2:R}")));
        var output = Path.Combine(directory, "report.xls");

        var result = XlsReportWriter.Generate(new ReportRequest(input, output, 0.7, DarkInputPath: dark));

        using var workbook = Load(output);
        Assert.True(Value(workbook, "H23") > 0);
        Assert.True(Value(workbook, "H24") > 0);
        Assert.True(Value(workbook, "H25") < 0);
        Assert.Contains(result.Warnings, warning => warning.Contains("отрицатель", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UpdatesMainPlotAxesAndKeepsLogarithmicDarkSeriesPositive()
    {
        var output = Path.Combine(directory, "report.xls");
        var input = Path.Combine(AppContext.BaseDirectory, "examples", "measurements.txt");
        XlsReportWriter.Generate(new ReportRequest(input, output, 2.341));

        using var workbook = Load(output);
        var sheet = (HSSFSheet)workbook.GetSheet("С=0.7");
        foreach (var chart in new[] { 1, 3 })
        {
            var axes = ChartAxes(sheet, chart);
            Assert.Equal(2, axes.Length);
            Assert.All(axes, axis =>
            {
                Assert.False(axis.IsAutomaticMinimum);
                Assert.False(axis.IsAutomaticMaximum);
                Assert.True(axis.IsAutomaticMajor);
                Assert.True(axis.IsAutomaticMinor);
            });
            Assert.Equal(-0.06817419179029338, axes[0].MinimumAxisValue, 12);
            Assert.Equal(1.431658027596161, axes[0].MaximumAxisValue, 12);
        }
        var zoomAxes = ChartAxes(sheet, 1);
        Assert.Equal(0.0027120244239338823, zoomAxes[1].MinimumAxisValue, 12);
        Assert.Equal(0.003361810396183939, zoomAxes[1].MaximumAxisValue, 12);
        var fullAxes = ChartAxes(sheet, 3);
        Assert.Equal(0, fullAxes[1].MinimumAxisValue);
        Assert.Equal(0.003473044643421256, fullAxes[1].MaximumAxisValue, 12);
        Assert.NotEmpty(ChartCache(sheet, 0, 1, 0));
        Assert.NotEmpty(ChartCache(sheet, 0, 2, 0));
        Assert.All(ChartCache(sheet, 0, 1, 0), value => Assert.True(value > 0));
        Assert.All(ChartCache(sheet, 0, 2, 0), value => Assert.True(value > 0));
    }

    [Fact]
    public void MainPlotAxesHaveReadableNumberFormatsWithoutChangingCachedPrecision()
    {
        var input = Path.Combine(AppContext.BaseDirectory, "examples", "measurements.txt");
        var output = Path.Combine(directory, "report.xls");
        var result = XlsReportWriter.Generate(new ReportRequest(input, output, 2.341));

        using var workbook = Load(output);
        var sheet = (HSSFSheet)workbook.GetSheet("С=0.7");
        var dataFormat = workbook.CreateDataFormat();
        foreach (var chart in new[] { 1, 3 })
        {
            var formats = ChartAxisFormats(sheet, chart);
            Assert.Equal(2, formats.Count);
            Assert.Equal("0.00", dataFormat.GetFormat(formats[0]));
            Assert.Equal("0.00000", dataFormat.GetFormat(formats[1]));
        }
        Assert.Equal(result.Calculations.MaximumPowerVoltage, ChartCache(sheet, 1, 2, 3).Single(), 12);
        Assert.Equal(result.Calculations.MaximumPowerCurrentDensity, ChartCache(sheet, 1, 1, 3).Single(), 12);
    }

    [Fact]
    public void SmallCurrentDensityUsesScientificAxisLabels()
    {
        var output = Path.Combine(directory, "report.xls");

        XlsReportWriter.Generate(new ReportRequest(WriteCurve("main.txt", 40), output, 1000));

        using var workbook = Load(output);
        var sheet = (HSSFSheet)workbook.GetSheet("С=0.7");
        var dataFormat = workbook.CreateDataFormat();
        foreach (var chart in new[] { 1, 3 })
        {
            var formats = ChartAxisFormats(sheet, chart);
            Assert.Equal("0.00E+00", dataFormat.GetFormat(formats[1]));
        }
        AssertNoErrors(sheet);
    }

    [Fact]
    public void WarnsAboutNegativeSintonResistance()
    {
        var sinton = Write("sinton.txt", 100, voltage => -0.01 * (voltage + 0.1), false, 1.4);
        var dark = Path.Combine(directory, "dark.txt");
        File.WriteAllText(dark, "0\t0\n1\t0\n2\t0");
        var output = Path.Combine(directory, "report.xls");

        var result = XlsReportWriter.Generate(new ReportRequest(WriteCurve("main.txt", 40), output, 0.7,
            DarkInputPath: dark, SintonInputPath: sinton));

        using var workbook = Load(output);
        Assert.True(Value(workbook, "H26") < 0);
        Assert.Contains(result.Warnings, warning => warning.Contains("Синтон", StringComparison.OrdinalIgnoreCase) &&
            warning.Contains("отрицатель", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FailedGenerationPreservesExistingOutputAndLeavesNoTemporaryFile()
    {
        var output = Path.Combine(directory, "report.xls");
        var original = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(output, original);
        var invalidTemplate = Path.Combine(directory, "invalid.xls");
        File.WriteAllText(invalidTemplate, "invalid workbook");

        Assert.Throws<InvalidDataException>(() => XlsReportWriter.Generate(new ReportRequest(WriteCurve("main.txt", 40), output,
            0.7, TemplatePath: invalidTemplate)));

        Assert.Equal(original, File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void FailedAtomicReplacementCleansTemporaryFile()
    {
        var output = Path.Combine(directory, "report.xls");
        Directory.CreateDirectory(output);
        var marker = Path.Combine(output, "existing.txt");
        File.WriteAllText(marker, "existing");

        Assert.Throws<IOException>(() => XlsReportWriter.Generate(new ReportRequest(WriteCurve("main.txt", 40), output, 0.7)));

        Assert.Equal("existing", File.ReadAllText(marker));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void RejectsOverwritingSourcesAndWrongOutputExtension()
    {
        var templatePath = Path.Combine(directory, "template.xls");
        using (var template = LoadTemplate())
            using (var stream = File.Create(templatePath))
                template.Write(stream);
        var original = File.ReadAllBytes(templatePath);
        var input = WriteCurve("main.txt", 40);

        var exception = Assert.Throws<ArgumentException>(() => XlsReportWriter.Generate(new ReportRequest(input,
            templatePath, 0.7, TemplatePath: templatePath)));
        Assert.Contains("исход", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, File.ReadAllBytes(templatePath));
        Assert.Throws<ArgumentException>(() => XlsReportWriter.Generate(new ReportRequest(input,
            Path.Combine(directory, "report.xlsx"), 0.7)));
    }

    [Fact]
    public void RejectsSourceOverwriteThroughASymbolicParentDirectory()
    {
        var real = Path.Combine(directory, "real");
        var alias = Path.Combine(directory, "alias");
        Directory.CreateDirectory(real);
        try
        {
            Directory.CreateSymbolicLink(alias, real);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            return;
        }
        var templatePath = Path.Combine(real, "source.xls");
        using (var template = LoadTemplate())
            using (var stream = File.Create(templatePath))
                template.Write(stream);
        var original = File.ReadAllBytes(templatePath);

        Assert.Throws<ArgumentException>(() => XlsReportWriter.Generate(new ReportRequest(WriteCurve("main.txt", 40),
            templatePath, 0.7, TemplatePath: Path.Combine(alias, "source.xls"))));

        Assert.Equal(original, File.ReadAllBytes(templatePath));
    }

    private string WriteCurve(string name, int count) => Write(name, count,
        voltage => -0.005 * (1 - voltage / 0.9), true, 1.2);

    private string WriteDark(string name, int count) => Write(name, count,
        voltage => 0.012 * voltage, false, 1.2);

    private string WriteSinton(string name, int count) => Write(name, count,
        voltage => -0.01 * (1 - voltage / 1.4), false, 1.4);

    private string Write(string name, int count, Func<double, double> current, bool withTime, double end)
    {
        var path = Path.Combine(directory, name);
        var rows = Enumerable.Range(0, count).Select(i =>
        {
            var voltage = -0.15 + (end + 0.15) * i / (count - 1);
            return voltage.ToString("R", CultureInfo.InvariantCulture) + "\t" +
                current(voltage).ToString("R", CultureInfo.InvariantCulture) +
                (withTime ? "\t" + (i * 0.1).ToString("R", CultureInfo.InvariantCulture) : "");
        });
        File.WriteAllText(path, string.Join("\n", rows));
        return path;
    }

    private static DisposableWorkbook Load(string path)
    {
        using var stream = File.OpenRead(path);
        return new DisposableWorkbook(stream);
    }

    private static DisposableWorkbook LoadTemplate()
    {
        using var stream = typeof(XlsReportWriter).Assembly.GetManifestResourceStream("SolarReport.Template.xls")!;
        return new DisposableWorkbook(stream);
    }

    private static ICell Cell(HSSFWorkbook workbook, string address)
    {
        var reference = new CellReference(address);
        return workbook.GetSheet("С=0.7").GetRow(reference.Row).GetCell(reference.Col);
    }

    private static double Value(HSSFWorkbook workbook, string address) => Cell(workbook, address).NumericCellValue;

    private sealed class DisposableWorkbook(Stream stream) : HSSFWorkbook(stream), IDisposable
    {
        public void Dispose() => Close();
    }

    private static bool IsMainRange(CellRangeAddressBase range) => range.FirstColumn is >= 0 and <= 5 &&
        range.FirstColumn == range.LastColumn && range.LastRow > range.FirstRow;

    private static string[] ChartGeometry(HSSFSheet sheet) => HSSFChart.GetSheetCharts(sheet)
        .Select(chart => $"{chart.Type}:{chart.ChartX}:{chart.ChartY}:{chart.ChartWidth}:{chart.ChartHeight}:{chart.Series.Length}")
        .ToArray();

    private static string[] ChartMarkers(HSSFSheet sheet) => HSSFChart.GetSheetCharts(sheet)
        .SelectMany(chart => chart.Series).SelectMany(series => new[] { series.GetDataValues(), series.GetDataCategoryLabels() })
        .Where(link => link is not null).SelectMany(link => link.FormulaOfLink.OfType<RefPtgBase>())
        .Select(reference => $"{reference.GetType().Name}:{reference.Row}:{reference.Column}").ToArray();

    private static double[] ChartCache(HSSFSheet sheet, int chartIndex, int cacheIndex, int seriesIndex)
    {
        var values = new List<double>();
        var currentChart = -1;
        var currentCache = -1;
        foreach (var recordBase in sheet.Sheet.Records)
        {
            if (recordBase is BOFRecord bof && bof.Type == BOFRecordType.Chart)
            {
                currentChart++;
                currentCache = -1;
            }
            if (recordBase is SeriesIndexRecord record)
                currentCache = record.Index;
            if (currentChart == chartIndex && currentCache == cacheIndex && recordBase is NumberRecord number &&
                number.Column == seriesIndex)
                values.Add(number.Value);
            if (recordBase is EOFRecord)
                currentCache = -1;
        }
        return values.ToArray();
    }

    private static ValueRangeRecord[] ChartAxes(HSSFSheet sheet, int chartIndex)
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
        return axes.ToArray();
    }

    private static Dictionary<short, short> ChartAxisFormats(HSSFSheet sheet, int chartIndex)
    {
        var formats = new Dictionary<short, short>();
        var currentChart = -1;
        short currentAxis = -1;
        foreach (var record in sheet.Sheet.Records)
        {
            if (record is BOFRecord bof && bof.Type == BOFRecordType.Chart)
            {
                currentChart++;
                currentAxis = -1;
            }
            if (record is AxisRecord axis)
                currentAxis = axis.AxisType;
            if (currentChart == chartIndex && currentAxis >= 0 && record is NPOI.HSSF.Record.Record format &&
                format.Sid == NumberFormatIndexRecord.sid)
                formats[currentAxis] = BitConverter.ToInt16(format.Serialize(), 4);
        }
        return formats;
    }

    private static string[] Snapshot(HSSFSheet sheet)
    {
        var values = new List<string>();
        foreach (IRow row in sheet)
            foreach (ICell cell in row)
                values.Add($"{cell.RowIndex}:{cell.ColumnIndex}:{cell.CellType}:" +
                    (cell.CellType == CellType.Formula ? cell.CellFormula : cell.ToString()));
        return values.ToArray();
    }

    private static void AssertNoErrors(ISheet sheet)
    {
        foreach (IRow row in sheet)
            foreach (ICell cell in row)
                Assert.False(cell.CellType == CellType.Error ||
                    cell.CellType == CellType.Formula && cell.CachedFormulaResultType == CellType.Error,
                    $"Ошибка формулы в {new CellReference(cell.RowIndex, cell.ColumnIndex).FormatAsString()}");
    }

    public void Dispose() => Directory.Delete(directory, true);
}
