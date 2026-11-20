param([Parameter(Mandatory = $true)][string]$OutDir)
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = if (Test-Path -LiteralPath $vswhere) {
    & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $vs) {
    Write-Warning 'MSVC x64 is unavailable. The build will use the WPF 3D fallback.'
    exit 0
}
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$directory = (New-Item -ItemType Directory -Path $OutDir -Force).FullName
$source = Join-Path $PSScriptRoot 'preview.cpp'
$dll = Join-Path $directory 'PoeMtxPreview.dll'
$object = Join-Path $directory 'preview.obj'
$import = Join-Path $directory 'PoeMtxPreview.lib'
$command = ('call "{0}" >nul && cl /nologo /O2 /W4 /EHsc /std:c++17 /MT /LD "{1}" /Fo"{2}" /Fe"{3}" /link /NOLOGO /IMPLIB:"{4}" d3d11.lib d3dcompiler.lib' -f $vcvars,$source,$object,$dll,$import)
& $env:ComSpec /d /c $command
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
