<#
.SYNOPSIS
    Builds, tests and packages LidFlow.

.DESCRIPTION
    One script, no prerequisites beyond the .NET SDK. Notably it does NOT need the
    Windows SDK or Visual Studio Build Tools: the HLSL is compiled at runtime by
    the d3dcompiler that ships with Windows, so there is no fxc step, and the
    project uses no C++ toolchain.

    Missing dependencies produce a precise message explaining what to install.
    Nothing is ever installed automatically.

.PARAMETER Configuration
    Build configuration. Release by default.

.PARAMETER Runtime
    Target runtime identifier. win-x64 by default.

.PARAMETER SkipTests
    Skip the unit test run.

.PARAMETER NoZip
    Produce the dist folder but no archives.

.PARAMETER SelfContained
    Bundle the .NET runtime so the build runs on a machine without .NET installed.
    On by default; -SelfContained:$false produces a much smaller framework-dependent
    build that requires the .NET Desktop Runtime.

.EXAMPLE
    .\BUILD-WINDOWS.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [string] $Runtime = 'win-x64',

    [switch] $SkipTests,

    [switch] $NoZip,

    [bool] $SelfContained = $true
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$MinimumSdkMajor = 8

$RepoRoot = $PSScriptRoot
$Solution = Join-Path $RepoRoot 'LidFlow.sln'
$AppProject = Join-Path $RepoRoot 'src/LidFlow.App/LidFlow.App.csproj'
$TestProject = Join-Path $RepoRoot 'tests/LidFlow.Core.Tests/LidFlow.Core.Tests.csproj'
$DistRoot = Join-Path $RepoRoot 'dist'
$PayloadDir = Join-Path $DistRoot 'LidFlow'

function Write-Step([string] $Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Good([string] $Message) {
    Write-Host "    $Message" -ForegroundColor Green
}

function Write-Note([string] $Message) {
    Write-Host "    $Message" -ForegroundColor DarkGray
}

function Fail([string] $Message, [string] $Remedy) {
    Write-Host ''
    Write-Host 'BUILD FAILED' -ForegroundColor Red
    Write-Host "  $Message" -ForegroundColor Red
    if ($Remedy) {
        Write-Host ''
        Write-Host 'How to fix it:' -ForegroundColor Yellow
        foreach ($line in $Remedy -split "`n") {
            Write-Host "  $line" -ForegroundColor Yellow
        }
    }
    Write-Host ''
    exit 1
}

# ---------------------------------------------------------------- environment

Write-Step 'Checking the build environment'

$isWindowsHost = $true
if (Test-Path 'variable:global:IsWindows') {
    $isWindowsHost = $IsWindows
} elseif ($PSVersionTable.PSEdition -eq 'Core') {
    $isWindowsHost = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
}

if (-not $isWindowsHost) {
    Fail 'LidFlow can only be packaged on Windows.' @'
LidFlow is a Windows desktop application. The projects can be COMPILED on Linux or
macOS (the app project sets EnableWindowsTargeting), but publishing a runnable
win-x64 build and producing the distributable requires a Windows host.

On Windows, run:
  .\BUILD-WINDOWS.cmd
'@
}

Write-Good "Windows $([System.Environment]::OSVersion.Version)"

if ([System.Environment]::OSVersion.Version.Build -lt 19041) {
    Write-Host '    WARNING: this machine is older than Windows 10 version 2004 (build 19041).' -ForegroundColor Yellow
    Write-Host '             LidFlow will build, but the overlay cannot be excluded from screen' -ForegroundColor Yellow
    Write-Host '             capture, which disables the opening animation. See docs/RESEARCH.md.' -ForegroundColor Yellow
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Fail 'The .NET SDK was not found (no "dotnet" on PATH).' @'
Install the .NET 8 SDK (or newer):
  https://dotnet.microsoft.com/download/dotnet/8.0

Or with winget:
  winget install Microsoft.DotNet.SDK.8

Then open a new terminal and run this script again.
'@
}

$sdkLines = @(& dotnet --list-sdks 2>$null)
if ($LASTEXITCODE -ne 0 -or $sdkLines.Count -eq 0) {
    Fail 'The "dotnet" command exists but reports no installed SDKs.' @'
You may have only the .NET Runtime installed, not the SDK. Install the SDK:
  https://dotnet.microsoft.com/download/dotnet/8.0
'@
}

$sdkMajors = @()
foreach ($line in $sdkLines) {
    if ($line -match '^(\d+)\.') {
        $sdkMajors += [int] $Matches[1]
    }
}

if (($sdkMajors | Measure-Object -Maximum).Maximum -lt $MinimumSdkMajor) {
    $found = ($sdkLines -join '; ')
    Fail "LidFlow needs the .NET $MinimumSdkMajor SDK or newer. Found: $found" @"
Install the .NET $MinimumSdkMajor SDK:
  https://dotnet.microsoft.com/download/dotnet/8.0
"@
}

Write-Good "dotnet SDK $(& dotnet --version)"
Write-Note 'No Windows SDK or C++ build tools are required: the shader is compiled at runtime.'

# Nothing about this build should phone home.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# ------------------------------------------------------------------- restore

Write-Step 'Restoring NuGet packages'

# Restored without a runtime identifier: NETSDK1134 forbids building a solution
# for a specific RID. The RID is applied at publish time, on the one project that
# actually produces a runnable binary.
& dotnet restore $Solution
if ($LASTEXITCODE -ne 0) {
    Fail 'Package restore failed.' @'
Most often this is no network access to nuget.org, or a proxy that needs
configuring. Check connectivity and retry:
  dotnet restore LidFlow.sln
'@
}
Write-Good 'Packages restored.'

# --------------------------------------------------------------------- build

Write-Step "Building ($Configuration)"

& dotnet build $Solution -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    Fail 'Compilation failed. The compiler output above lists the errors.' ''
}
Write-Good 'Build succeeded.'

# --------------------------------------------------------------------- tests

if (-not $SkipTests) {
    Write-Step 'Running unit tests'

    # The test project is platform-neutral on purpose, so it is built without the
    # win-x64 runtime identifier here.
    & dotnet test $TestProject -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        Fail 'Unit tests failed.' @'
The failing tests are listed above. Re-run just the tests with:
  dotnet test tests\LidFlow.Core.Tests\LidFlow.Core.Tests.csproj
'@
    }
    Write-Good 'All tests passed.'
} else {
    Write-Note 'Tests skipped (-SkipTests).'
}

# ------------------------------------------------------------------- publish

Write-Step "Publishing $Runtime (self-contained: $SelfContained)"

if (Test-Path $DistRoot) {
    Remove-Item $DistRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $PayloadDir -Force | Out-Null

$publishArgs = @(
    'publish', $AppProject,
    '-c', $Configuration,
    '-r', $Runtime,
    '-o', $PayloadDir,
    "-p:SelfContained=$($SelfContained.ToString().ToLowerInvariant())",
    '-p:PublishReadyToRun=true',
    '-p:PublishSingleFile=false',
    '-p:DebugType=none',
    '-p:GenerateDocumentationFile=false',
    '--nologo'
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Fail 'Publish failed.' ''
}

$exe = Join-Path $PayloadDir 'LidFlow.exe'
if (-not (Test-Path $exe)) {
    Fail "Publish reported success but $exe is missing." ''
}

Write-Good "Published to $PayloadDir"

# --------------------------------------------------------------- extra assets

Write-Step 'Copying distribution files'

foreach ($item in @('README.md', 'LICENSE', 'config.example.json', 'INSTALL.cmd', 'UNINSTALL.cmd')) {
    $source = Join-Path $RepoRoot $item
    if (Test-Path $source) {
        Copy-Item $source -Destination $PayloadDir -Force
        Write-Note "  $item"
    }
}

$docsSource = Join-Path $RepoRoot 'docs'
if (Test-Path $docsSource) {
    Copy-Item $docsSource -Destination (Join-Path $PayloadDir 'docs') -Recurse -Force
    Write-Note '  docs\'
}

# ------------------------------------------------------------------ archives

if (-not $NoZip) {
    Write-Step 'Creating archives'

    $binaryZip = Join-Path $DistRoot "LidFlow-Windows-$($Runtime.Replace('win-', '')).zip"
    Compress-Archive -Path (Join-Path $PayloadDir '*') -DestinationPath $binaryZip -Force
    Write-Good "  $(Split-Path $binaryZip -Leaf)"

    # Source archive, excluding build output, VCS and IDE state.
    $sourceZip = Join-Path $DistRoot 'LidFlow-Windows-Source.zip'
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) ("lidflow-src-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    try {
        $exclude = @('.git', '.github', 'bin', 'obj', 'dist', 'artifacts', '.vs', '.vscode', '.idea', 'node_modules')

        Get-ChildItem -Path $RepoRoot -Force | Where-Object {
            $exclude -notcontains $_.Name
        } | ForEach-Object {
            Copy-Item $_.FullName -Destination $staging -Recurse -Force
        }

        # .github holds the CI definition and belongs in the source archive, but
        # nothing else from the excluded list does.
        $workflows = Join-Path $RepoRoot '.github'
        if (Test-Path $workflows) {
            Copy-Item $workflows -Destination $staging -Recurse -Force
        }

        Get-ChildItem -Path $staging -Recurse -Force -Directory |
            Where-Object { $_.Name -in @('bin', 'obj', '.vs', '.idea') } |
            Sort-Object FullName -Descending |
            ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $sourceZip -Force
        Write-Good "  $(Split-Path $sourceZip -Leaf)"
    } finally {
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# -------------------------------------------------------------------- summary

$size = [math]::Round((Get-ChildItem $PayloadDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host ''
Write-Host 'BUILD SUCCEEDED' -ForegroundColor Green
Write-Host ''
Write-Host "  Executable   $exe"
Write-Host "  Payload      $PayloadDir ($size MB)"
if (-not $NoZip) {
    Write-Host "  Archives     $DistRoot"
}
Write-Host ''
Write-Host '  Run it:      dist\LidFlow\LidFlow.exe'
Write-Host '  Preview it:  dist\LidFlow\LidFlow.exe --preview-close'
Write-Host '  Install it:  dist\LidFlow\INSTALL.cmd'
Write-Host ''
exit 0
