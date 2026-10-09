var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Dev2Lead_Api>("dev2lead-api", launchProfileName: "Dev2Lead.Api")
    .WithEndpoint("http", endpoint => endpoint.IsProxied = false)
    .WithHttpHealthCheck("/health");

var desktopDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "Dev2Lead"));
builder.AddExecutable("dev2lead-desktop", "dotnet", desktopDirectory,
        "run", "--project", "Dev2Lead.csproj", "-f", "net10.0-windows10.0.19041.0")
    .WithEnvironment("Backend__Url", api.GetEndpoint("http"))
    .WaitFor(api)
    .WithExplicitStart();

builder.Build().Run();
