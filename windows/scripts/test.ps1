# Runs the TypelessCore test suite (xUnit), including the mock server that injects failures into a 130 s recording.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
dotnet test tests\TypelessCore.Tests @args
exit $LASTEXITCODE
