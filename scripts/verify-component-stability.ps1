param(
    [ValidateRange(1, 100)] [int] $Iterations = 5,
    [ValidateSet("net10.0", "net8.0")] [string] $Framework = "net10.0",
    [string] $ResultsDirectory = ""
)
$ErrorActionPreference = "Stop"
$repository = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repository "tests/MandoCode.Tests/MandoCode.Tests.csproj"
if (!$ResultsDirectory) { $ResultsDirectory = Join-Path $repository "artifacts/component-stability/$Framework" }
& dotnet build $project -c Release -f $Framework --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Test build failed ($LASTEXITCODE)." }
for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    Write-Host "Component stability: $Framework run $iteration of $Iterations"
    & dotnet test $project -c Release -f $Framework --no-build --filter "Category=Component" `
        --results-directory $ResultsDirectory --logger "trx;LogFileName=component-$iteration.trx" `
        --blame-hang-timeout 2m --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Component run $iteration failed ($LASTEXITCODE). See $ResultsDirectory." }
}
