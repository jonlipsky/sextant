# Publishes the Sextant app's activities bundle to activities/sextant/ (relative to this directory).
# The host provides the ProcessStack SDK, so the bundle must carry no ProcessStack.*.dll.
# Extra arguments are passed to `dotnet publish`.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Push-Location $PSScriptRoot
try {
    $output = 'activities/sextant'
    if (Test-Path $output) {
        Remove-Item -Recurse -Force $output
    }
    dotnet publish src/Sextant.ProcessStack.Activities -c Release -o $output @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    Write-Host "Published ${output}:"
    Get-ChildItem -Name $output | ForEach-Object { Write-Host $_ }

    $sdk = @(Get-ChildItem -Path $output -Filter 'ProcessStack.*.dll' -Name)
    if ($sdk.Count -gt 0) {
        throw "The bundle must not ship ProcessStack SDK assemblies: $($sdk -join ', ')"
    }
    foreach ($required in 'Sextant.ProcessStack.Activities.dll', 'Sextant.Core.dll') {
        if (-not (Test-Path (Join-Path $output $required))) {
            throw "The bundle is missing $required."
        }
    }
}
finally {
    Pop-Location
}
