param(
    [switch] $Containers,
    [string] $PackageVersion
)

$ErrorActionPreference = "Stop"

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Invoke-DotNet tool restore
Invoke-DotNet restore Aspire.ModularAppHosts.slnx
Invoke-DotNet format Aspire.ModularAppHosts.slnx --verify-no-changes --no-restore
Invoke-DotNet build Aspire.ModularAppHosts.slnx --configuration Release --no-restore
Invoke-DotNet test Aspire.ModularAppHosts.slnx --configuration Release --no-build --no-restore
$packageVersionArguments = @()
if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
    $packageVersionArguments += "-p:PackageVersion=$PackageVersion"
}
Invoke-DotNet pack src/Aspire.Hosting.ModularAppHosts/Aspire.Hosting.ModularAppHosts.csproj `
    --configuration Release --no-build --no-restore --output artifacts @packageVersionArguments
Invoke-DotNet pack src/Aspire.Hosting.ModularAppHosts.Testing/Aspire.Hosting.ModularAppHosts.Testing.csproj `
    --configuration Release --no-build --no-restore --output artifacts @packageVersionArguments
Invoke-DotNet pack templates/Aspire.Hosting.ModularAppHosts.Templates.csproj `
    --configuration Release --no-build --no-restore --output artifacts @packageVersionArguments

if ($Containers) {
    $previousMode = $env:ESHOP_E2E_MODE
    $previousApiKey = $env:Parameters__orders_api_key
    try {
        $env:ESHOP_E2E_MODE = "compose"
        $env:Parameters__orders_api_key = "e2e-orders-key"
        Invoke-DotNet test samples/E2ETesting/EShop.E2E.Tests/EShop.E2E.Tests.csproj `
            --configuration Release --no-build --no-restore
    }
    finally {
        $env:ESHOP_E2E_MODE = $previousMode
        $env:Parameters__orders_api_key = $previousApiKey
    }
}
