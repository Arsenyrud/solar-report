$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$applicationDirectory = Join-Path $root "artifacts/SolarReport-win-x64"
$application = Join-Path $applicationDirectory "SolarReport.exe"
$result = Join-Path $applicationDirectory "reports/result.xls"

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class ReportWindow
{
    private delegate bool Callback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, Callback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, string lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    public static IntPtr Find(IntPtr parent, string classPrefix, string caption)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (handle, parameter) =>
        {
            var type = new StringBuilder(256);
            var text = new StringBuilder(256);
            GetClassName(handle, type, type.Capacity);
            GetWindowText(handle, text, text.Capacity);
            if (IsWindowVisible(handle) && type.ToString().StartsWith(classPrefix, StringComparison.Ordinal)
                && (caption == null || text.ToString() == caption))
            {
                found = handle;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static void SetText(IntPtr handle, string text) => SendMessage(handle, 0x000C, IntPtr.Zero, text);
    public static void Click(IntPtr handle) => SendMessage(handle, 0x00F5, IntPtr.Zero, IntPtr.Zero);
}
'@

if (Test-Path $result) { Remove-Item $result }
$process = Start-Process $application -PassThru
try {
    if (-not $process.WaitForInputIdle(20000)) { throw "Окно программы не открылось" }
    $process.Refresh()
    if ($process.HasExited -or $process.MainWindowHandle -eq 0) { throw "Программа завершилась до открытия окна" }
    $area = [ReportWindow]::Find($process.MainWindowHandle, "WindowsForms10.EDIT", $null)
    $button = [ReportWindow]::Find($process.MainWindowHandle, "WindowsForms10.BUTTON", "Сформировать отчет")
    if ($area -eq 0 -or $button -eq 0) { throw "Не найдены поле площади или кнопка расчета" }
    [ReportWindow]::SetText($area, "2,341")
    [ReportWindow]::Click($button)
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path $result) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw "Программа завершилась во время расчета" }
    }
    if (-not (Test-Path $result)) { throw "Окно не сформировало отчет" }
    $bytes = [System.IO.File]::ReadAllBytes($result)
    if ($bytes.Length -lt 8 -or [BitConverter]::ToString($bytes, 0, 8) -ne "D0-CF-11-E0-A1-B1-1A-E1") {
        throw "Результат не является файлом XLS"
    }
    Write-Host "Windows: запуск окна, ввод площади и создание XLS проверены"
} finally {
    if (-not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force }
    }
}
