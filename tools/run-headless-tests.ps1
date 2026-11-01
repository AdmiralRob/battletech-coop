param([ValidateSet('MovementTests', 'NetTests')][string]$Suite = 'MovementTests')
# PowerShell 7 fallback for machines without the dotnet SDK. Start a fresh process per suite.
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$paths = @(Get-ChildItem (Join-Path $project "tests/$Suite") -Filter '*.cs' | Select-Object -ExpandProperty FullName)
if ($Suite -eq 'MovementTests') {
    $paths += Join-Path $project 'src/BattletechCoop/Sync/MovementOrderGuard.cs'
    $paths += Join-Path $project 'src/BattletechCoop/Patches/InvocationLogPatch.cs'
    $paths += Join-Path $project 'src/BattletechCoop/Sync/PendingSelectionOrders.cs'
    $paths += Join-Path $project 'src/BattletechCoop/Patches/GuestSelectionOrderPatch.cs'
} else {
    $paths += Get-ChildItem (Join-Path $project 'src/BattletechCoop/Net/Core') -Filter '*.cs' | Select-Object -ExpandProperty FullName
}
$types = Add-Type -Path $paths -PassThru
$program = $types | Where-Object Name -eq 'Program'
$main = $program.GetMethod('Main', [Reflection.BindingFlags]'Static,NonPublic')
$failures = $main.Invoke($null, @())
if ($failures -ne 0) { throw "$Suite failed: $failures test(s)" }
