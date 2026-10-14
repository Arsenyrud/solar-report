using System.Globalization;
using SolarReport.Core;

namespace SolarReport.Cli;

internal static class Program
{
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("ru-RU");

    private static int Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        try
        {
            var options = ParseArguments(args);
            var settings = AppSettings.Load(AppContext.BaseDirectory);
            settings = ApplyOptions(settings, options);
            settings.ValidateFiles();

            Console.WriteLine("Создание отчета...");
            var result = XlsReportWriter.Generate(settings.CreateRequest(options.Area));
            var calculation = result.Calculations;
            Console.WriteLine($"Отчет создан: {result.OutputPath}");
            Console.WriteLine($"Точек измерения: {result.MeasurementCount}");
            Console.WriteLine($"Jsc: {(calculation.ShortCircuitCurrentDensity * 1000).ToString("F4", DisplayCulture)} мА/см²");
            Console.WriteLine($"Voc: {calculation.OpenCircuitVoltage.ToString("F4", DisplayCulture)} В");
            Console.WriteLine($"FF: {calculation.FillFactor.ToString("P2", DisplayCulture)}");
            Console.WriteLine($"КПД: {calculation.Efficiency.ToString("P2", DisplayCulture)}");
            foreach (var warning in result.Warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)))
                Console.WriteLine($"Предупреждение: {warning}");
            return 0;
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine($"Ошибка: {error.Message}");
            Console.Error.WriteLine("Для справки: --help");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Не удалось создать отчет: {error.Message}");
            return 1;
        }
    }

    private static ParsedOptions ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (key is not ("--area" or "--input" or "--output" or "--template" or
                "--reference-current-density" or "--dark" or "--sinton"))
                throw new ArgumentException($"Неизвестный параметр: {key}");
            if (values.ContainsKey(key))
                throw new ArgumentException($"Параметр {key} указан несколько раз");
            if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(args[index]))
                throw new ArgumentException($"После {key} укажите значение");
            values.Add(key, args[index]);
        }

        if (!values.TryGetValue("--area", out var areaText))
            throw new ArgumentException("Укажите площадь образца в см²: --area 1,25");
        if (!AppSettings.TryParsePositiveNumber(areaText, out var area))
            throw new ArgumentException("Площадь должна быть положительным конечным числом. Пример: --area 1,25");
        return new ParsedOptions(area, values);
    }

    private static AppSettings ApplyOptions(AppSettings settings, ParsedOptions options)
    {
        var values = options.Values;
        double? reference = settings.ReferenceCurrentDensity;
        if (values.TryGetValue("--reference-current-density", out var referenceText))
        {
            if (!AppSettings.TryParsePositiveNumber(referenceText, out var parsedReference))
                throw new ArgumentException("--reference-current-density должен быть положительным конечным числом в А/см²");
            reference = parsedReference;
        }

        return settings with
        {
            InputPath = ResolveOption("--input", settings.InputPath)!,
            OutputPath = ResolveOption("--output", settings.OutputPath)!,
            TemplatePath = ResolveOption("--template", settings.TemplatePath),
            DarkInputPath = ResolveOption("--dark", settings.DarkInputPath),
            SintonInputPath = ResolveOption("--sinton", settings.SintonInputPath),
            ReferenceCurrentDensity = reference
        };

        string? ResolveOption(string key, string? fallback) => values.TryGetValue(key, out var path)
            ? Path.GetFullPath(path, Directory.GetCurrentDirectory())
            : fallback;
    }

    private sealed record ParsedOptions(double Area, IReadOnlyDictionary<string, string> Values);

    private const string HelpText = """
        SolarReport: отчет о солнечном элементе в формате Excel .xls

        Запуск:
          SolarReport.Cli --area 1,25 [параметры]

        Обязательный параметр:
          --area ЧИСЛО                       Площадь образца, см²; число больше нуля.

        Дополнительные параметры:
          --input ФАЙЛ                       Файл световой ВАХ.
          --output ФАЙЛ.xls                  Путь готового отчета.
          --template ФАЙЛ.xls                Внешний шаблон вместо встроенного.
          --reference-current-density ЧИСЛО  Эталонная плотность тока, А/см².
          --dark ФАЙЛ                        Файл темновой ВАХ.
          --sinton ФАЙЛ                      Файл измерений Sinton.
          --help                             Показать справку.

        Числа допускают точку или запятую в качестве десятичного разделителя.
        Для запуска без интерактивного ввода --area нужен всегда.
        Пути, явно заданные параметрами, считаются от текущей рабочей папки.
        Пути из settings.json считаются от папки программы.
        Файл settings.json рядом с программой необязателен. Допустимые поля:
          InputPath, OutputPath, TemplatePath, ReferenceCurrentDensity,
          DarkInputPath, SintonInputPath.
        По умолчанию: inputs/measurements.txt и reports/result.xls рядом с программой,
        встроенный шаблон. Пути с пробелами заключайте в кавычки.
        Ошибки возвращают ненулевой код завершения.
        """;
}
