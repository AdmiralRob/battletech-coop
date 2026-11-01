param(
    [string]$ManagedDir = (Join-Path $PSScriptRoot '../local/Managed'),
    [string]$OutputDir = (Join-Path $PSScriptRoot '../local/build')
)
# Offline build using PowerShell 7's bundled Roslyn compiler and locally copied game
# references. No deployment, package downloads, or writes to the game installation.
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $project 'src/BattletechCoop'
$parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp7_3)
$trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
foreach ($file in Get-ChildItem $source -Recurse -Filter '*.cs' | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }) {
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file.FullName), $parseOptions, $file.FullName))
}
$version = (Get-Content (Join-Path $source 'mod.json') -Raw | ConvertFrom-Json).Version
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('[assembly: System.Reflection.AssemblyVersion("' + $version + '.0")]', $parseOptions))
$references = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($file in Get-ChildItem $ManagedDir -Filter '*.dll') {
    $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$options = $options.WithAssemblyIdentityComparer([Microsoft.CodeAnalysis.DesktopAssemblyIdentityComparer]::Default)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('BattletechCoop', $trees, $references, $options)
$output = (New-Item -ItemType Directory -Force $OutputDir).FullName
$stream = [IO.File]::Create((Join-Path $output 'BattletechCoop.dll'))
try { $result = $compilation.Emit($stream) } finally { $stream.Dispose() }
$result.Diagnostics | ForEach-Object { $_.ToString() } | Set-Content (Join-Path $output 'build-diagnostics.txt')
$result.Diagnostics | Where-Object Severity -ne 'Hidden' | ForEach-Object { $_.ToString() } | Sort-Object -Unique | Write-Output
if (!$result.Success) { throw 'Compilation failed; do not use this output.' }
Copy-Item (Join-Path $source 'mod.json') $output
Write-Output "Built BattletechCoop $version at $output"
