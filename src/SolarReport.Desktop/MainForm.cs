using System.Diagnostics;
using System.Globalization;
using SolarReport.Core;

namespace SolarReport.Desktop;

internal sealed class MainForm : Form
{
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("ru-RU");
    private readonly TextBox areaInput = new() { Width = 170, PlaceholderText = "Например, 1,25" };
    private readonly Label inputPathLabel = CreateLabel();
    private readonly Label outputPathLabel = CreateLabel();
    private readonly Label statusLabel = CreateLabel();
    private readonly Label jscValue = CreateLabel();
    private readonly Label vocValue = CreateLabel();
    private readonly Label fillFactorValue = CreateLabel();
    private readonly Label efficiencyValue = CreateLabel();
    private readonly Button generateButton = new() { Text = "Сформировать отчет", AutoSize = true, Padding = new Padding(12, 5, 12, 5) };
    private readonly Button openReportButton = new() { Text = "Открыть отчет", AutoSize = true, Enabled = false };
    private readonly Button openFolderButton = new() { Text = "Открыть папку", AutoSize = true, Enabled = false };
    private readonly TextBox details = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        AccessibleName = "Сообщения и предупреждения",
        TabStop = false,
        Visible = false
    };
    private bool isGenerating;
    private string? lastOutputPath;

    public MainForm()
    {
        Text = "SolarReport";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 620);
        MinimumSize = new Size(580, 630);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 9
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 8; index++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = CreateLabel("Отчет о солнечном элементе");
        title.Font = new Font(Font, FontStyle.Bold);
        title.Margin = new Padding(0, 0, 0, 18);
        layout.Controls.Add(title, 0, 0);

        var pathPanel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ColumnCount = 1
        };
        pathPanel.Controls.Add(CreateLabel("Исходные измерения"));
        pathPanel.Controls.Add(inputPathLabel);
        var outputTitle = CreateLabel("Готовый отчет");
        outputTitle.Margin = new Padding(0, 12, 0, 2);
        pathPanel.Controls.Add(outputTitle);
        pathPanel.Controls.Add(outputPathLabel);
        pathPanel.Margin = new Padding(0, 0, 0, 20);
        inputPathLabel.ForeColor = outputPathLabel.ForeColor = Color.FromArgb(80, 80, 80);
        layout.Controls.Add(pathPanel, 0, 1);

        layout.Controls.Add(CreateLabel("Площадь образца"), 0, 2);
        var areaPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            WrapContents = false
        };
        areaInput.AccessibleName = "Площадь образца в квадратных сантиметрах";
        areaInput.TabIndex = 0;
        areaPanel.Controls.Add(areaInput);
        var areaUnit = CreateLabel("см²");
        areaUnit.Margin = new Padding(6, 6, 0, 0);
        areaPanel.Controls.Add(areaUnit);
        layout.Controls.Add(areaPanel, 0, 3);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            WrapContents = true
        };
        actions.Margin = new Padding(0, 12, 0, 12);
        actions.Controls.Add(generateButton);
        actions.Controls.Add(openReportButton);
        actions.Controls.Add(openFolderButton);
        generateButton.TabIndex = 1;
        openReportButton.TabIndex = 2;
        openFolderButton.TabIndex = 3;
        layout.Controls.Add(actions, 0, 4);

        statusLabel.Margin = new Padding(0, 0, 0, 14);
        statusLabel.AccessibleName = "Состояние создания отчета";
        layout.Controls.Add(statusLabel, 0, 5);

        var resultGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Font = Font
        };
        resultGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        resultGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        AddResultRow(resultGrid, "Jsc", jscValue, 0);
        AddResultRow(resultGrid, "Voc", vocValue, 1);
        AddResultRow(resultGrid, "Фактор заполнения (FF)", fillFactorValue, 2);
        AddResultRow(resultGrid, "КПД", efficiencyValue, 3);
        resultGrid.Margin = new Padding(0, 0, 0, 14);
        layout.Controls.Add(resultGrid, 0, 6);

        var hint = CreateLabel("Параметры файлов можно изменить в settings.json рядом с программой");
        hint.ForeColor = Color.FromArgb(90, 90, 90);
        hint.Margin = new Padding(0, 0, 0, 10);
        layout.Controls.Add(hint, 0, 7);
        layout.Controls.Add(details, 0, 8);
        Controls.Add(layout);

        AcceptButton = generateButton;
        generateButton.Click += async (_, _) => await GenerateReportAsync();
        openReportButton.Click += (_, _) => OpenPath(lastOutputPath);
        openFolderButton.Click += (_, _) => OpenPath(lastOutputPath is null ? null : Path.GetDirectoryName(lastOutputPath));
        FormClosing += PreventClosingDuringGeneration;
        ClientSizeChanged += (_, _) =>
            inputPathLabel.MaximumSize = outputPathLabel.MaximumSize = new Size(Math.Max(400, ClientSize.Width - 60), 0);
        LoadInitialSettings();
    }

    private void LoadInitialSettings()
    {
        try
        {
            var settings = AppSettings.Load(AppContext.BaseDirectory);
            ShowPaths(settings);
            var savedArea = settings.ReadLastArea();
            if (savedArea.HasValue)
                areaInput.Text = savedArea.Value.ToString("G", DisplayCulture);
            statusLabel.Text = "Введите площадь и сформируйте отчет";
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    private async Task GenerateReportAsync()
    {
        if (isGenerating)
            return;
        if (!AppSettings.TryParsePositiveNumber(areaInput.Text, out var area))
        {
            ShowError("Введите площадь больше нуля в см². Используйте точку или запятую, например 1,25");
            areaInput.Focus();
            areaInput.SelectAll();
            return;
        }

        isGenerating = true;
        generateButton.Enabled = false;
        areaInput.ReadOnly = true;
        openReportButton.Enabled = openFolderButton.Enabled = false;
        lastOutputPath = null;
        details.Clear();
        details.Visible = false;
        ClearResults();
        statusLabel.ForeColor = Color.FromArgb(50, 50, 50);
        statusLabel.Text = "Создание отчета. Дождитесь завершения...";

        try
        {
            var settings = AppSettings.Load(AppContext.BaseDirectory);
            ShowPaths(settings);
            settings.ValidateFiles();
            var request = settings.CreateRequest(area);
            var result = await Task.Run(() => XlsReportWriter.Generate(request));
            var warnings = result.Warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).ToList();
            try
            {
                settings.SaveLastArea(area);
            }
            catch (Exception error)
            {
                warnings.Add($"Отчет создан, но площадь не сохранена: {error.Message}");
            }

            var calculation = result.Calculations;
            jscValue.Text = $"{(calculation.ShortCircuitCurrentDensity * 1000).ToString("F4", DisplayCulture)} мА/см²";
            vocValue.Text = $"{calculation.OpenCircuitVoltage.ToString("F4", DisplayCulture)} В";
            fillFactorValue.Text = calculation.FillFactor.ToString("P2", DisplayCulture);
            efficiencyValue.Text = calculation.Efficiency.ToString("P2", DisplayCulture);
            statusLabel.ForeColor = Color.FromArgb(25, 100, 50);
            statusLabel.Text = $"Отчет создан. Точек измерения: {result.MeasurementCount}";
            lastOutputPath = result.OutputPath;
            openReportButton.Enabled = openFolderButton.Enabled = true;
            if (warnings.Count > 0)
            {
                details.Text = "Предупреждения:" + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, warnings);
                details.Visible = true;
            }
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
        finally
        {
            isGenerating = false;
            generateButton.Enabled = true;
            areaInput.ReadOnly = false;
        }
    }

    private void PreventClosingDuringGeneration(object? sender, FormClosingEventArgs eventArgs)
    {
        if (!isGenerating)
            return;
        eventArgs.Cancel = true;
        statusLabel.Text = "Дождитесь завершения создания отчета, затем закройте окно";
    }

    private void OpenPath(string? path)
    {
        if (path is null || isGenerating)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"Не удалось открыть файл или папку.{Environment.NewLine}{error.Message}",
                "SolarReport", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void ShowPaths(AppSettings settings)
    {
        inputPathLabel.Text = settings.InputPath;
        outputPathLabel.Text = settings.OutputPath;
        inputPathLabel.MaximumSize = outputPathLabel.MaximumSize = new Size(Math.Max(400, ClientSize.Width - 60), 0);
    }

    private void ShowError(string message)
    {
        statusLabel.ForeColor = Color.FromArgb(165, 30, 35);
        statusLabel.Text = "Не удалось создать отчет";
        details.Text = message;
        details.Visible = true;
    }

    private void ClearResults()
    {
        jscValue.Text = vocValue.Text = fillFactorValue.Text = efficiencyValue.Text = "";
    }

    private static Label CreateLabel(string text = "") => new()
    {
        Text = text,
        AutoSize = true,
        Dock = DockStyle.Top,
        Margin = new Padding(0, 0, 0, 4),
        UseMnemonic = false
    };

    private static void AddResultRow(TableLayoutPanel grid, string caption, Label value, int row)
    {
        grid.Controls.Add(CreateLabel(caption), 0, row);
        value.Font = new Font(grid.Font, FontStyle.Bold);
        grid.Controls.Add(value, 1, row);
    }
}
