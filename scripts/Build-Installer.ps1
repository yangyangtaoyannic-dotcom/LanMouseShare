param(
    [string]$Version = "0.1.41"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dist = Join-Path $root "dist\LanMouseShare-$Version"
$setupSource = Join-Path $root "src\Setup\Program.cs"
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
Copy-Item -Path (Join-Path $root "src\Desktop.Agent\bin\Release\LanMouseShare.Agent.pdb") -Destination (Join-Path $dist "Agent") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $root "src\Desktop.Agent\bin\Release\LanMouseShare.Core.dll") -Destination (Join-Path $dist "Agent") -Force
Copy-Item -Path (Join-Path $root "src\Desktop.Agent\bin\Release\LanMouseShare.Core.pdb") -Destination (Join-Path $dist "Agent") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $root "src\Desktop.Agent\bin\Release\LanMouseShare.Input.dll") -Destination (Join-Path $dist "Agent") -Force
Copy-Item -Path (Join-Path $root "src\Desktop.Agent\bin\Release\LanMouseShare.Input.pdb") -Destination (Join-Path $dist "Agent") -Force -ErrorAction SilentlyContinue

Copy-Item -Path (Join-Path $root "src\Host.Service\bin\Release\LanMouseShare.Service.exe") -Destination (Join-Path $dist "Service") -Force
Copy-Item -Path (Join-Path $root "src\Host.Service\bin\Release\LanMouseShare.Service.pdb") -Destination (Join-Path $dist "Service") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $root "src\Host.Service\bin\Release\LanMouseShare.Core.dll") -Destination (Join-Path $dist "Service") -Force
Copy-Item -Path (Join-Path $root "src\Host.Service\bin\Release\LanMouseShare.Core.pdb") -Destination (Join-Path $dist "Service") -Force -ErrorAction SilentlyContinue

Copy-Item -Path (Join-Path $root "scripts\*") -Destination (Join-Path $dist "Scripts") -Recurse -Force
Copy-Item -Path (Join-Path $root "docs\*") -Destination (Join-Path $dist "Docs") -Recurse -Force
Copy-Item -Path (Join-Path $root "docs\PACKAGE_README.md") -Destination (Join-Path $dist "README.md") -Force

$resources = @()
Get-ChildItem -Path (Join-Path $dist "Agent") -File | ForEach-Object {
    $resources += "/resource:$($_.FullName),Agent.$($_.Name)"
}
Get-ChildItem -Path (Join-Path $dist "Service") -File | ForEach-Object {
    $resources += "/resource:$($_.FullName),Service.$($_.Name)"
}
Get-ChildItem -Path (Join-Path $dist "Scripts") -File | ForEach-Object {
    $resources += "/resource:$($_.FullName),Scripts.$($_.Name)"
}
Get-ChildItem -Path (Join-Path $dist "Docs") -File | ForEach-Object {
    $resources += "/resource:$($_.FullName),Docs.$($_.Name)"
}
$resources += "/resource:$(Join-Path $dist "README.md"),Root.README.md"

& $csc /nologo /target:winexe /optimize+ /win32manifest:$manifest /out:$output /reference:System.Windows.Forms.dll /reference:Microsoft.CSharp.dll $resources $setupSource
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Get-Item $output

if (Test-Path $sourceZip) {
    Remove-Item -LiteralPath $sourceZip -Force
}

$sourceStage = Join-Path ([System.IO.Path]::GetTempPath()) ("LanMouseShare-source-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $sourceStage | Out-Null
try {
    foreach ($name in @("src", "tests", "docs", "scripts")) {
        Copy-Item -Path (Join-Path $root $name) -Destination (Join-Path $sourceStage $name) -Recurse -Force -Exclude "bin","obj"
    }

    foreach ($name in @("LanMouseShare.sln", ".gitignore")) {
        $path = Join-Path $root $name
        if (Test-Path $path) {
            Copy-Item -Path $path -Destination (Join-Path $sourceStage $name) -Force
        }
    }

    Get-ChildItem -Path $sourceStage -Recurse -Directory -Include bin,obj,dist,.vs | Remove-Item -Recurse -Force
    Get-ChildItem -Path $sourceStage -Recurse -File -Include *.log,*.cache,*.user,*.suo | Remove-Item -Force
    Compress-Archive -Path (Join-Path $sourceStage "*") -DestinationPath $sourceZip -Force
}
finally {
    if (Test-Path $sourceStage) {
        Remove-Item -LiteralPath $sourceStage -Recurse -Force
    }
}

Get-Item $sourceZip
