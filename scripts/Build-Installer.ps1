param(
    [string]$Version = "0.2.0-beta-fix24"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dist = Join-Path $root "dist\LanMouseShare-$Version"
$setupSource = Join-Path $root "src\Setup\Program.cs"
$setupAssemblyInfo = Join-Path $root "src\Setup\AssemblyInfo.cs"
$manifest = Join-Path $root "src\Setup\app.manifest"
$output = Join-Path $dist "LanMouseShareSetup.exe"
$sourceZip = Join-Path $dist "LanMouseShare-$Version-source.zip"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (!(Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null
foreach ($name in @("Agent", "Service", "Scripts", "Docs")) {
    New-Item -ItemType Directory -Path (Join-Path $dist $name) -Force | Out-Null
}

Copy-Item -Path (Join-Path $root "src\Desktop.Agent\bin\Release\LanMouseShare.Agent.exe") -Destination (Join-Path $dist "Agent") -Force
Copy-Item -Path (Join-Path $root "src\Desktop.Agent\bin\Release\LanMouseShare.Core.dll") -Destination (Join-Path $dist "Agent") -Force
Copy-Item -Path (Join-Path $root "src\Desktop.Agent\bin\Release\LanMouseShare.Input.dll") -Destination (Join-Path $dist "Agent") -Force

Copy-Item -Path (Join-Path $root "src\Host.Service\bin\Release\LanMouseShare.Service.exe") -Destination (Join-Path $dist "Service") -Force
Copy-Item -Path (Join-Path $root "src\Host.Service\bin\Release\LanMouseShare.Core.dll") -Destination (Join-Path $dist "Service") -Force

Copy-Item -Path (Join-Path $root "scripts\*") -Destination (Join-Path $dist "Scripts") -Recurse -Force
foreach ($devScript in @("Test-Releases.ps1", "Upload-Releases.ps1")) {
    $devPath = Join-Path $dist ("Scripts\" + $devScript)
    if (Test-Path $devPath) {
        Remove-Item -LiteralPath $devPath -Force
    }
}
Copy-Item -Path (Join-Path $root "docs\*") -Destination (Join-Path $dist "Docs") -Recurse -Force
Copy-Item -Path (Join-Path $root "docs\PACKAGE_README.md") -Destination (Join-Path $dist "README.md") -Force
Copy-Item -Path (Join-Path $root "PROJECT_GUIDE.md") -Destination (Join-Path $dist "PROJECT_GUIDE.md") -Force

# Quick start guide keeps its non-ASCII file name; find it without hard-coding Chinese literals
# so this script stays safe for every Windows PowerShell code page.
$quickStart = Get-ChildItem -Path (Join-Path $root "docs") -File -Filter *.md |
    Where-Object { $_.Name -notmatch '^[A-Za-z0-9_.-]+\.md$' } |
    Select-Object -First 1
if ($quickStart) {
    Copy-Item -Path $quickStart.FullName -Destination (Join-Path $dist $quickStart.Name) -Force
}

# The installer carries one compressed payload instead of dozens of loose resources.
# Some antivirus engines delete freshly built self-extractors that embed many other
# executables, and a single zip keeps the installer far smaller as well.
$payloadStage = Join-Path ([System.IO.Path]::GetTempPath()) ("LanMouseShare-payload-" + [Guid]::NewGuid().ToString("N"))
$payloadZip = Join-Path $payloadStage "payload.zip"
New-Item -ItemType Directory -Path $payloadStage | Out-Null
try {
    foreach ($name in @("Agent", "Service", "Scripts", "Docs")) {
        Copy-Item -Path (Join-Path $dist $name) -Destination (Join-Path $payloadStage $name) -Recurse -Force
    }

    foreach ($fileName in @("README.md", "PROJECT_GUIDE.md")) {
        Copy-Item -Path (Join-Path $dist $fileName) -Destination (Join-Path $payloadStage $fileName) -Force
    }
    if ($quickStart) {
        Copy-Item -Path $quickStart.FullName -Destination (Join-Path $payloadStage $quickStart.Name) -Force
    }

    Compress-Archive -Path (Join-Path $payloadStage "*") -DestinationPath $payloadZip -Force

    # fix18：安装包也带上应用图标（csc 直接编译，不读 Setup.csproj 的 ApplicationIcon）
    $setupIcon = Join-Path $root "src\Desktop.Agent\Resources\app.ico"
    $iconArg = @()
    if (Test-Path $setupIcon) {
        $iconArg = @("/win32icon:$setupIcon")
    }

    & $csc /nologo /target:winexe /optimize+ /win32manifest:$manifest $iconArg /out:$output /reference:System.Windows.Forms.dll /reference:Microsoft.CSharp.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/resource:$payloadZip,Root.payload.zip" $setupSource $setupAssemblyInfo
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    if (Test-Path $payloadStage) {
        Remove-Item -LiteralPath $payloadStage -Recurse -Force
    }
}

Get-Item $output

if (Test-Path $sourceZip) {
    Remove-Item -LiteralPath $sourceZip -Force
}

$sourceStage = Join-Path ([System.IO.Path]::GetTempPath()) ("LanMouseShare-source-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $sourceStage | Out-Null
try {
    foreach ($name in @("src", "tests", "docs", "scripts", "patches")) {
        Copy-Item -Path (Join-Path $root $name) -Destination (Join-Path $sourceStage $name) -Recurse -Force -Exclude "bin","obj"
    }

    foreach ($name in @("LanMouseShare.sln", "PROJECT_GUIDE.md", ".gitignore")) {
        $path = Join-Path $root $name
        if (Test-Path $path) {
            Copy-Item -Path $path -Destination (Join-Path $sourceStage $name) -Force
        }
    }

    Get-ChildItem -Path $sourceStage -Recurse -Directory -Include bin,obj,dist,.vs | Remove-Item -Recurse -Force
    Get-ChildItem -Path $sourceStage -Recurse -File -Include *.log,*.cache,*.user,*.suo | Remove-Item -Force
    # 发布包内再带一份完整源码目录（用户要求：源码放在一个新文件夹中）
    $sourceDir = Join-Path $dist "source"
    if (Test-Path $sourceDir) {
        Remove-Item -LiteralPath $sourceDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $sourceDir -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceStage "*") -Destination $sourceDir -Recurse -Force

    Compress-Archive -Path (Join-Path $sourceStage "*") -DestinationPath $sourceZip -Force
}
finally {
    if (Test-Path $sourceStage) {
        Remove-Item -LiteralPath $sourceStage -Recurse -Force
    }
}

Get-Item $sourceZip
