param([string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet restore SolarReport.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Не удалось восстановить зависимости" }
    dotnet test tests/SolarReport.Tests/SolarReport.Tests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Тесты не прошли" }
    $destination = Join-Path $root "artifacts/SolarReport-$Runtime"
    if ($Runtime -ne "win-x64") { throw "Для этого выпуска подготовлена сборка win-x64" }
    dotnet publish src/SolarReport.Desktop/SolarReport.Desktop.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:RestoreLockedMode=true -o $destination
    if ($LASTEXITCODE -ne 0) { throw "Не удалось собрать программу" }
    New-Item -ItemType Directory -Path "$destination/inputs", "$destination/licenses", "$destination/docs" -Force | Out-Null
    Copy-Item examples/measurements.txt "$destination/inputs/measurements.txt"
    Copy-Item settings.example.json "$destination/settings.json"
    Copy-Item README.md, LICENSE, THIRD_PARTY_NOTICES.md $destination
    Copy-Item licenses/* "$destination/licenses" -Force
    Copy-Item docs/* "$destination/docs" -Force
    $package = Join-Path $root "artifacts/.package-$([Guid]::NewGuid().ToString('N'))"
    try {
        New-Item -ItemType Directory -Path "$package/inputs" -Force | Out-Null
        Copy-Item "$destination/SolarReport.exe", README.md, LICENSE, THIRD_PARTY_NOTICES.md $package
        Copy-Item examples/measurements.txt "$package/inputs/measurements.txt"
        Copy-Item settings.example.json "$package/settings.json"
        Copy-Item licenses, docs $package -Recurse
        Compress-Archive -Path "$package/*" -DestinationPath "artifacts/SolarReport-$Runtime.zip" -Force
    } finally {
        if (Test-Path $package) { Remove-Item $package -Recurse -Force }
    }
    Write-Host "Готово: artifacts/SolarReport-$Runtime.zip"
} finally {
    Pop-Location
}
