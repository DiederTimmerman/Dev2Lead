$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\Dev2Lead.Api\Dev2Lead.Api.csproj'
dotnet run --project $project --launch-profile Dev2Lead.Api
if ($LASTEXITCODE -ne 0) {
    throw "Dev2Lead backend exited with code $LASTEXITCODE. See the .NET output above."
}
