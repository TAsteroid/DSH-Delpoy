# Build DSH-Deploy.exe
#
# Produces a single self-contained Windows executable with no NuGet or .NET SDK
# requirement, using the C# compiler that ships with the .NET Framework. That is
# deliberate: the audience for this tool is a machine that may have no developer
# tooling at all, so the build must not need any either.
#
# NOTE: this file is intentionally ASCII-only. Windows PowerShell 5.1 reads .ps1
# files as ANSI unless they carry a UTF-8 BOM, which would corrupt non-ASCII text.

[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$IncludeDebugInfo
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$sourceDir = Join-Path $root 'src\DSHDeploy'
$assetsDir = Join-Path $root 'assets'
$iconPath = Join-Path $assetsDir 'deepseek-bowl.ico'
$manifestPath = Join-Path $sourceDir 'app.manifest'
$distDir = Join-Path $root 'dist'
$objDir = Join-Path $root 'build\obj'
$outputExe = Join-Path $distDir 'DSH-Deploy.exe'

# The icon this tool must carry. Verified before every build so the shipped exe can
# never silently pick up a substituted or corrupted asset.
$expectedIconSha256 = 'db20bea4cfd97296b5eac4808dd512174505b2c6921e4669f28ca2e3d8cf45b4'

function Write-Step($message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Find-Compiler {
    $frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64'
    if (-not (Test-Path $frameworkRoot)) {
        $frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework'
    }
    $compiler = Get-ChildItem -Path $frameworkRoot -Filter 'csc.exe' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Directory.Name.TrimStart('v')) } -Descending |
        Select-Object -First 1
    if ($null -eq $compiler) {
        throw 'C# compiler (csc.exe) not found. Install the .NET Framework 4.x.'
    }
    return $compiler.FullName
}

function Assert-Icon {
    if (-not (Test-Path $iconPath)) {
        throw "Missing icon file: $iconPath"
    }
    $hash = (Get-FileHash -Path $iconPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $expectedIconSha256) {
        throw "Icon checksum mismatch. Expected $expectedIconSha256 but found $hash"
    }
    Write-Host "    icon SHA-256 verified: $hash" -ForegroundColor Green
}

Write-Step 'Checking build inputs'
Assert-Icon
if (-not (Test-Path $manifestPath)) { throw "Missing manifest: $manifestPath" }

$compilerPath = Find-Compiler
Write-Host "    compiler: $compilerPath"

if ($Clean) {
    Write-Step 'Cleaning previous output'
    foreach ($path in @($objDir, $distDir)) {
        if (Test-Path $path) { Remove-Item -Path $path -Recurse -Force }
    }
}

New-Item -ItemType Directory -Path $objDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

Write-Step 'Collecting sources'
$sources = @(Get-ChildItem -Path $sourceDir -Filter '*.cs' -Recurse | Select-Object -ExpandProperty FullName)
if ($sources.Count -eq 0) { throw "No source files found under $sourceDir" }
$sources | ForEach-Object { Write-Host ('    ' + $_.Substring($root.Length + 1)) }

Write-Step 'Preparing the icon for embedding'
# The icon is attached two ways, with no resource compiler needed:
#   /win32icon  -> the exe's icon group, used by Explorer, the taskbar and the UAC prompt
#   /resource   -> the raw .ico, loaded at runtime for the wizard's header
# resgen.exe is not used: it ships with the Windows SDK, not with the .NET Framework.
$iconCopy = Join-Path $objDir 'deepseek-bowl.ico'
Copy-Item -Path $iconPath -Destination $iconCopy -Force
Write-Host '    icon staged (no .resources step required)'

Write-Step 'Generating assembly metadata'
$metadataPath = Join-Path $objDir 'AssemblyInfo.cs'
$metadata = @'
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("DSH Deploy")]
[assembly: AssemblyDescription("DeepSeek Harness one-click deployer")]
[assembly: AssemblyProduct("DSH Deploy")]
[assembly: AssemblyCompany("DSH Deploy")]
[assembly: AssemblyCopyright("MIT")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: ComVisible(false)]
'@
[System.IO.File]::WriteAllText($metadataPath, $metadata, (New-Object System.Text.UTF8Encoding($false)))

Write-Step 'Compiling'
$cscArgs = New-Object System.Collections.Generic.List[string]
$cscArgs.Add('/nologo')
$cscArgs.Add('/target:winexe')
$cscArgs.Add('/platform:x64')
$cscArgs.Add('/codepage:65001')
# The in-box compiler may be as old as C# 5.0, so no /langversion is passed and the
# sources deliberately avoid C# 6+ syntax. Every path is quoted because a workspace
# path may contain spaces.
$cscArgs.Add('/win32icon:"' + $iconPath + '"')
$cscArgs.Add('/win32manifest:"' + $manifestPath + '"')
$cscArgs.Add('/resource:"' + $iconPath + '",deepseek-bowl.ico')
$cscArgs.Add('/reference:System.dll')
$cscArgs.Add('/reference:System.Core.dll')
$cscArgs.Add('/reference:System.Drawing.dll')
$cscArgs.Add('/reference:System.Windows.Forms.dll')
$cscArgs.Add('/reference:System.Net.Http.dll')
$cscArgs.Add('/reference:System.IO.Compression.dll')
$cscArgs.Add('/reference:System.IO.Compression.FileSystem.dll')
$cscArgs.Add('/reference:System.Security.dll')
if ($IncludeDebugInfo) {
    $cscArgs.Add('/debug+')
    $cscArgs.Add('/define:DEBUG')
} else {
    $cscArgs.Add('/optimize+')
}
$cscArgs.Add('/out:"' + $outputExe + '"')
foreach ($source in $sources) { $cscArgs.Add('"' + $source + '"') }
$cscArgs.Add('"' + $metadataPath + '"')

$responseFile = Join-Path $objDir 'csc.rsp'
[System.IO.File]::WriteAllLines($responseFile, $cscArgs, (New-Object System.Text.UTF8Encoding($false)))

$compilerOutput = & $compilerPath ('@' + $responseFile) 2>&1
$compilerExit = $LASTEXITCODE
$compilerOutput | ForEach-Object { Write-Host "    $_" }
if ($compilerExit -ne 0 -or -not (Test-Path $outputExe)) {
    throw "Compilation failed (exit code $compilerExit)"
}

Write-Step 'Verifying output'
$exeInfo = Get-Item $outputExe
$exeHash = (Get-FileHash -Path $outputExe -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ('    output:  ' + $exeInfo.FullName)
Write-Host ('    size:    {0:N0} bytes' -f $exeInfo.Length)
Write-Host ('    SHA-256: ' + $exeHash)

# Confirm the produced exe really carries the icon and the embedded resource names.
$bytes = [System.IO.File]::ReadAllBytes($outputExe)
$ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
$hasIconName = $ascii.Contains('deepseek-bowl.ico')
if ($hasIconName) {
    Write-Host '    embedded icon resource: present' -ForegroundColor Green
} else {
    Write-Host '    warning: icon resource name not found in the output' -ForegroundColor Yellow
}

# Verify the exe's icon group is real by asking the shell to extract it back.
try {
    Add-Type -AssemblyName System.Drawing
    $extracted = [System.Drawing.Icon]::ExtractAssociatedIcon($outputExe)
    if ($null -ne $extracted) {
        Write-Host ('    icon group: present (' + $extracted.Width + 'x' + $extracted.Height + ')') -ForegroundColor Green
    } else {
        Write-Host '    warning: no icon group could be extracted from the exe' -ForegroundColor Yellow
    }
} catch {
    Write-Host ('    warning: could not verify the icon group: ' + $_.Exception.Message) -ForegroundColor Yellow
}

Write-Step 'Done'
Write-Host ('    self-test: "' + $outputExe + '" --self-test')
