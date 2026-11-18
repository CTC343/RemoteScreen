param(
    [ValidateSet('All','Controller','Receiver')]
    [string] $Target = 'All',
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'dist'),
    [string] $ReferenceDirectory
)
$ErrorActionPreference = 'Stop'
$edition = 'Win10'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Install .NET Framework 4.8 before building.' }
if (-not $ReferenceDirectory) {
    $roots = @($env:ProgramFiles, [Environment]::GetEnvironmentVariable('ProgramFiles(x86)'))
    $roots += (Join-Path "$($env:SystemDrive)\" 'Program Files (x86)')
    $ReferenceDirectory = $roots | Where-Object { $_ } | ForEach-Object {
        Join-Path $_ 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
    } | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'mscorlib.dll') } | Select-Object -First 1
}
if (-not $ReferenceDirectory) { throw 'Install .NET Framework 4.8 Developer Pack or specify -ReferenceDirectory.' }
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$references = foreach ($name in 'mscorlib','System','System.Core','System.Drawing','System.Windows.Forms') {
    $dll = Join-Path $ReferenceDirectory ($name + '.dll')
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Missing reference: $dll" }
    "/reference:$dll"
}
$manifest = Join-Path $PSScriptRoot 'App.manifest'
$flags = '/nologo','/noconfig','/nostdlib+','/codepage:65001','/utf8output','/target:winexe',
    '/platform:anycpu','/optimize+',"/win32manifest:$manifest"
$sources = 'AssemblyInfo.cs','Wire.cs','Desktop.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }

foreach ($role in @(
    @{ Name = 'Controller'; Source = 'View.cs' },
    @{ Name = 'Receiver'; Source = 'Share.cs' }
)) {
    if ($Target -ne 'All' -and $Target -ne $role.Name) { continue }
    $exe = Join-Path $OutputDirectory ($edition + '-' + $role.Name + '.exe')
    & $compiler "/out:$exe" @flags @references @sources (Join-Path $PSScriptRoot $role.Source)
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $exe" }
    Get-Item -LiteralPath $exe
}
