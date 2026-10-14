using System.Globalization;
using System.Text.Json;

namespace SolarReport.Core;

public sealed record AppSettings(
    string ApplicationDirectory,
    string InputPath,
    string OutputPath,
    string? TemplatePath = null,
    double? ReferenceCurrentDensity = null,
    string? DarkInputPath = null,
    string? SintonInputPath = null)
{
    public static AppSettings Load(string applicationDirectory)
    {
        var directory = Path.GetFullPath(applicationDirectory);
        var configurationPath = Path.Combine(directory, "settings.json");
        var configuration = new Configuration();
        if (Directory.Exists(configurationPath))
            throw new InvalidDataException($"settings.json должен быть файлом, а не папкой: {configurationPath}");
        if (File.Exists(configurationPath))
        {
            try
            {
                configuration = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(configurationPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException($"Файл settings.json пуст или содержит null: {configurationPath}. Укажите объект JSON с настройками.");
            }
            catch (JsonException error)
            {
                var location = error.LineNumber.HasValue
                    ? $" Строка {error.LineNumber.Value + 1}, позиция {(error.BytePositionInLine ?? 0) + 1}."
                    : "";
                throw new InvalidDataException($"Не удалось прочитать settings.json: {configurationPath}.{location} Проверьте синтаксис JSON и типы значений.", error);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Не удалось прочитать settings.json: {configurationPath}. Проверьте доступ к файлу. {error.Message}", error);
            }
        }

        if (configuration.ReferenceCurrentDensity.HasValue &&
            !IsPositiveFinite(configuration.ReferenceCurrentDensity.Value))
            throw new InvalidDataException("ReferenceCurrentDensity в settings.json должен быть положительным конечным числом в А/см².");

        return new AppSettings(
            directory,
            ResolvePath(configuration.InputPath, Path.Combine("inputs", "measurements.txt"), "InputPath"),
            ResolvePath(configuration.OutputPath, Path.Combine("reports", "result.xls"), "OutputPath"),
            ResolveOptionalPath(configuration.TemplatePath, "TemplatePath"),
            configuration.ReferenceCurrentDensity,
            ResolveOptionalPath(configuration.DarkInputPath, "DarkInputPath"),
            ResolveOptionalPath(configuration.SintonInputPath, "SintonInputPath"));

        string ResolvePath(string? path, string fallback, string key)
        {
            path ??= fallback;
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException($"{key} в settings.json не может быть пустым. Укажите путь или удалите это поле.");
            try
            {
                return Path.GetFullPath(path, directory);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidDataException($"Некорректный путь {key} в settings.json: {path}. Проверьте имя файла.", error);
            }
        }

        string? ResolveOptionalPath(string? path, string key) => path is null ? null : ResolvePath(path, "", key);
    }

    public static bool TryParsePositiveNumber(string? text, out double value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(text) &&
            double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            IsPositiveFinite(value);
    }

    public void ValidateFiles()
    {
        RequireFile(InputPath, "InputPath", false);
        if (TemplatePath is not null)
            RequireFile(TemplatePath, "TemplatePath", true);
        if (DarkInputPath is not null)
            RequireFile(DarkInputPath, "DarkInputPath", true);
        if (SintonInputPath is not null)
            RequireFile(SintonInputPath, "SintonInputPath", true);
    }

    public ReportRequest CreateRequest(double area)
    {
        ValidateArea(area);
        return new ReportRequest(InputPath, OutputPath, area, TemplatePath,
            ReferenceCurrentDensity, DarkInputPath, SintonInputPath);
    }

    public double? ReadLastArea()
    {
        var path = Path.Combine(ApplicationDirectory, "area.txt");
        if (!File.Exists(path))
            return null;
        return TryParsePositiveNumber(File.ReadAllText(path), out var area) ? area : null;
    }

    public void SaveLastArea(double area)
    {
        ValidateArea(area);
        var path = Path.GetFullPath(Path.Combine(ApplicationDirectory, "area.txt"));
        EnsureAreaStorageIsSeparate(path);
        var temporaryPath = Path.Combine(ApplicationDirectory, $".area.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, area.ToString("R", CultureInfo.InvariantCulture));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A failed cleanup must preserve the original write error.
            }
        }
    }

    private void EnsureAreaStorageIsSeparate(string areaPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var resolvedAreaPath = ResolvePhysicalPath(areaPath);
        foreach (var (key, configuredPath) in new (string, string?)[]
        {
            ("InputPath", InputPath),
            ("OutputPath", OutputPath),
            ("TemplatePath", TemplatePath),
            ("DarkInputPath", DarkInputPath),
            ("SintonInputPath", SintonInputPath)
        })
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
                continue;
            var fullPath = Path.GetFullPath(configuredPath);
            if (string.Equals(areaPath, fullPath, comparison) ||
                string.Equals(resolvedAreaPath, ResolvePhysicalPath(fullPath), comparison))
                throw new InvalidDataException($"Файл area.txt совпадает с {key}. Измените путь в настройках");
        }
    }

    private static string ResolvePhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var resolvedPath = Path.GetPathRoot(fullPath)!;
        foreach (var segment in fullPath[resolvedPath.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(resolvedPath, segment);
            FileSystemInfo? info = Directory.Exists(candidate) ? new DirectoryInfo(candidate)
                : File.Exists(candidate) ? new FileInfo(candidate) : null;
            var target = info?.ResolveLinkTarget(returnFinalTarget: true);
            resolvedPath = target is null ? candidate : ResolvePhysicalPath(target.FullName);
        }
        return resolvedPath;
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private static void ValidateArea(double area)
    {
        if (!IsPositiveFinite(area))
            throw new ArgumentOutOfRangeException(nameof(area), "Площадь образца должна быть положительным конечным числом в см².");
    }

    private static void RequireFile(string path, string key, bool optional)
    {
        if (File.Exists(path))
            return;
        var advice = optional
            ? $"Проверьте {key} в settings.json или параметр запуска. Если эти данные не нужны, удалите поле {key} из настроек."
            : "Поместите файл измерений по этому пути или измените InputPath в settings.json либо параметр --input.";
        throw new FileNotFoundException($"Не найден или недоступен файл {key}: {path}. {advice}", path);
    }

    private sealed class Configuration
    {
        public Configuration() { }

        public string? InputPath { get; init; }
        public string? OutputPath { get; init; }
        public string? TemplatePath { get; init; }
        public double? ReferenceCurrentDensity { get; init; }
        public string? DarkInputPath { get; init; }
        public string? SintonInputPath { get; init; }
    }
}
