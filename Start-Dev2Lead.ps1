$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\Dev2Lead\Dev2Lead.csproj'
dotnet run --project $project -f net10.0-windows10.0.19041.0
if ($LASTEXITCODE -ne 0) {
    throw "Dev2Lead exited with code $LASTEXITCODE. See the .NET output above."
}
