#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet restore SolarReport.slnx --locked-mode
dotnet test tests/SolarReport.Tests/SolarReport.Tests.csproj -c Release --no-restore
dotnet build SolarReport.slnx -c Release --no-restore
dotnet run --project src/SolarReport.Cli -c Release --no-build -- \
  --input examples/measurements.txt --output artifacts/example.xls --area 2.341
