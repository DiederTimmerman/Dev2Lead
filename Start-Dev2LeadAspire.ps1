$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\Dev2Lead.AppHost\Dev2Lead.AppHost.csproj'
dotnet run --project $project --launch-profile http
if ($LASTEXITCODE -ne 0) {
    throw "Dev2Lead Aspire exited with code $LASTEXITCODE. See the .NET output above."
}
