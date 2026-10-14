using SolarReport.Core;
using Xunit;

namespace SolarReport.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "solar-settings-" + Guid.NewGuid().ToString("N"));

    public AppSettingsTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("1.25", 1.25)]
    [InlineData("1,25", 1.25)]
    [InlineData(" 2,5 ", 2.5)]
    [InlineData("1e-2", 0.01)]
    public void ParsesPositiveFiniteNumbersWithEitherDecimalSeparator(string text, double expected)
    {
        Assert.True(AppSettings.TryParsePositiveNumber(text, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    [InlineData("1,2.3")]
    [InlineData("1 000")]
    public void RejectsInvalidAreas(string text) => Assert.False(AppSettings.TryParsePositiveNumber(text, out _));

    [Fact]
    public void ResolvesDefaultsFromApplicationDirectory()
    {
        var settings = AppSettings.Load(directory);

        Assert.Equal(Path.Combine(directory, "inputs", "measurements.txt"), settings.InputPath);
        Assert.Equal(Path.Combine(directory, "reports", "result.xls"), settings.OutputPath);
        Assert.Null(settings.TemplatePath);
    }

    [Fact]
    public void ResolvesConfiguredPathsFromApplicationDirectory()
    {
        File.WriteAllText(Path.Combine(directory, "settings.json"), """
            {
              "InputPath": "data/light.txt",
              "OutputPath": "results/panel.xls",
              "TemplatePath": "custom.xls",
              "ReferenceCurrentDensity": 40.5,
              "DarkInputPath": "data/dark.txt",
              "SintonInputPath": "data/sinton.txt"
            }
            """);

        var settings = AppSettings.Load(directory);

        Assert.Equal(Path.Combine(directory, "data", "light.txt"), settings.InputPath);
        Assert.Equal(Path.Combine(directory, "results", "panel.xls"), settings.OutputPath);
        Assert.Equal(Path.Combine(directory, "custom.xls"), settings.TemplatePath);
        Assert.Equal(Path.Combine(directory, "data", "dark.txt"), settings.DarkInputPath);
        Assert.Equal(Path.Combine(directory, "data", "sinton.txt"), settings.SintonInputPath);
        Assert.Equal(40.5, settings.ReferenceCurrentDensity);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    public void MalformedConfigurationNamesSettingsFile(string json)
    {
        File.WriteAllText(Path.Combine(directory, "settings.json"), json);

        var error = Assert.Throws<InvalidDataException>(() => AppSettings.Load(directory));

        Assert.Contains("settings.json", error.Message);
    }

    [Theory]
    [InlineData("{\"ReferenceCurrentDensity\":0}", "ReferenceCurrentDensity")]
    [InlineData("{\"ReferenceCurrentDensity\":-1}", "ReferenceCurrentDensity")]
    [InlineData("{\"InputPath\":\" \"}", "InputPath")]
    public void InvalidConfigurationExplainsSetting(string json, string key)
    {
        File.WriteAllText(Path.Combine(directory, "settings.json"), json);

        var error = Assert.Throws<InvalidDataException>(() => AppSettings.Load(directory));

        Assert.Contains(key, error.Message);
    }

    [Theory]
    [InlineData("TemplatePath", "template.xls")]
    [InlineData("DarkInputPath", "dark.txt")]
    [InlineData("SintonInputPath", "sinton.txt")]
    public void MissingOptionalFileNamesConfiguredSetting(string key, string fileName)
    {
        File.WriteAllText(Path.Combine(directory, "measurements.txt"), "0 1");
        File.WriteAllText(Path.Combine(directory, "settings.json"), $"{{\"InputPath\":\"measurements.txt\",\"{key}\":\"{fileName}\"}}");
        var settings = AppSettings.Load(directory);

        var error = Assert.Throws<FileNotFoundException>(settings.ValidateFiles);

        Assert.Contains(key, error.Message);
        Assert.Equal(Path.Combine(directory, fileName), error.FileName);
    }

    [Fact]
    public void SavedAreaSurvivesRejectedReplacement()
    {
        var settings = AppSettings.Load(directory);
        settings.SaveLastArea(1.25);

        Assert.Throws<ArgumentOutOfRangeException>(() => settings.SaveLastArea(double.NaN));
        Assert.Equal(1.25, settings.ReadLastArea());
    }

    [Fact]
    public void InvalidSavedAreaDoesNotBecomeInput()
    {
        File.WriteAllText(Path.Combine(directory, "area.txt"), "-3");

        Assert.Null(AppSettings.Load(directory).ReadLastArea());
    }

    [Fact]
    public void SavingAreaCannotReplaceConfiguredMeasurementFile()
    {
        var sourcePath = Path.Combine(directory, "area.txt");
        const string measurements = "0 0.05 1\n0.8 0.02 2\n1 0 3\n";
        File.WriteAllText(sourcePath, measurements);
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"InputPath\":\"area.txt\"}");
        var settings = AppSettings.Load(directory);
        Assert.Equal(3, MeasurementReader.Read(sourcePath).Count);

        var error = Record.Exception(() => settings.SaveLastArea(2.5));

        Assert.Equal(measurements, File.ReadAllText(sourcePath));
        Assert.IsType<InvalidDataException>(error);
    }

    [Theory]
    [InlineData("InputPath")]
    [InlineData("OutputPath")]
    [InlineData("TemplatePath")]
    [InlineData("DarkInputPath")]
    [InlineData("SintonInputPath")]
    public void SavingAreaRejectsEveryConfiguredFileCollision(string key)
    {
        var sourcePath = Path.Combine(directory, "area.txt");
        const string contents = "0 0.05 1\n0.8 0.02 2\n1 0 3\n";
        File.WriteAllText(sourcePath, contents);
        var equivalentPath = Path.Combine(directory, "unused", "..", "area.txt");
        var settings = AppSettings.Load(directory);
        settings = key switch
        {
            "InputPath" => settings with { InputPath = equivalentPath },
            "OutputPath" => settings with { OutputPath = equivalentPath },
            "TemplatePath" => settings with { TemplatePath = equivalentPath },
            "DarkInputPath" => settings with { DarkInputPath = equivalentPath },
            "SintonInputPath" => settings with { SintonInputPath = equivalentPath },
            _ => throw new InvalidOperationException()
        };

        var error = Record.Exception(() => settings.SaveLastArea(2.5));

        Assert.Equal(contents, File.ReadAllText(sourcePath));
        Assert.Contains(key, Assert.IsType<InvalidDataException>(error).Message);
    }

    [Fact]
    public void SavingAreaRejectsInputSymbolicLinkToAreaFile()
    {
        var sourcePath = Path.Combine(directory, "area.txt");
        const string contents = "0 0.05 1\n0.8 0.02 2\n1 0 3\n";
        File.WriteAllText(sourcePath, contents);
        var aliasPath = Path.Combine(directory, "light-link.txt");
        File.CreateSymbolicLink(aliasPath, sourcePath);
        var settings = AppSettings.Load(directory) with { InputPath = aliasPath };

        var error = Record.Exception(() => settings.SaveLastArea(2.5));

        Assert.Equal(contents, File.ReadAllText(sourcePath));
        Assert.IsType<InvalidDataException>(error);
    }

    [Fact]
    public void SavingAreaRejectsInputThroughDirectorySymbolicLink()
    {
        var actualDirectory = Path.Combine(directory, "actual");
        Directory.CreateDirectory(actualDirectory);
        var sourcePath = Path.Combine(actualDirectory, "area.txt");
        const string contents = "0 0.05 1\n0.8 0.02 2\n1 0 3\n";
        File.WriteAllText(sourcePath, contents);
        var aliasDirectory = Path.Combine(directory, "alias");
        Directory.CreateSymbolicLink(aliasDirectory, actualDirectory);
        var settings = AppSettings.Load(actualDirectory) with { InputPath = Path.Combine(aliasDirectory, "area.txt") };

        var error = Record.Exception(() => settings.SaveLastArea(2.5));

        Assert.Equal(contents, File.ReadAllText(sourcePath));
        Assert.IsType<InvalidDataException>(error);
    }

    [Fact]
    public void SavingAreaComparesNamesUsingPlatformCaseRules()
    {
        var sourcePath = Path.Combine(directory, "AREA.TXT");
        const string contents = "0 0.05 1\n0.8 0.02 2\n1 0 3\n";
        File.WriteAllText(sourcePath, contents);
        var settings = AppSettings.Load(directory) with { InputPath = sourcePath };

        if (OperatingSystem.IsWindows())
            Assert.Throws<InvalidDataException>(() => settings.SaveLastArea(2.5));
        else
        {
            settings.SaveLastArea(2.5);
            Assert.Equal(2.5, settings.ReadLastArea());
        }

        Assert.Equal(contents, File.ReadAllText(sourcePath));
    }

    [Fact]
    public void RequestIncludesAllConfiguredFilesAndArea()
    {
        var settings = AppSettings.Load(directory) with
        {
            TemplatePath = Path.Combine(directory, "template.xls"),
            DarkInputPath = Path.Combine(directory, "dark.txt"),
            SintonInputPath = Path.Combine(directory, "sinton.txt"),
            ReferenceCurrentDensity = 41
        };

        var request = settings.CreateRequest(2.5);

        Assert.Equal(2.5, request.Area);
        Assert.Equal(settings.InputPath, request.InputPath);
        Assert.Equal(settings.OutputPath, request.OutputPath);
        Assert.Equal(settings.TemplatePath, request.TemplatePath);
        Assert.Equal(settings.DarkInputPath, request.DarkInputPath);
        Assert.Equal(settings.SintonInputPath, request.SintonInputPath);
        Assert.Equal(41, request.ReferenceCurrentDensity);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
