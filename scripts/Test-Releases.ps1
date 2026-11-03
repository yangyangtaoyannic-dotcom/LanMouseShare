$DIST = 'C:\Users\yangyang\Documents\局域网键盘鼠标共享\dist'
$versionDirs = Get-ChildItem -Path $DIST -Directory | Sort-Object Name

Write-Host ('找到 ' + $versionDirs.Count + ' 个版本目录') -ForegroundColor Green
Write-Host '=========================================='

$totalSize = 0

foreach ($dir in $versionDirs) {
    $folderName = $dir.Name
    $version    = $folderName -replace '^LanMouseShare-', ''
    $tag        = 'v' + $version

    $dirSize = (Get-ChildItem -Path $dir.FullName -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    $totalSize += $dirSize

    $sourceZip = Get-ChildItem -Path $dir.FullName -Filter '*source.zip' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    $setupExe  = Get-ChildItem -Path $dir.FullName -Filter '*Setup.exe' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1

    $line = '[' + $tag + '] 目录: ' + ([math]::Round($dirSize/1MB, 1)) + ' MB'

    if ($sourceZip) {
        $line += ' | source.zip: ' + ([math]::Round($sourceZip.Length/1MB, 1)) + ' MB'
    }
    if ($setupExe) {
        $line += ' | Setup.exe: ' + ([math]::Round($setupExe.Length/1MB, 1)) + ' MB'
    }
    if ((-not $sourceZip) -and (-not $setupExe)) {
        $line += ' | (无特定文件，将打包上传)'
    }

    Write-Host $line
}

Write-Host '=========================================='
Write-Host ('所有版本目录总大小: ' + ([math]::Round($totalSize/1MB, 1)) + ' MB (' + ([math]::Round($totalSize/1GB, 2)) + ' GB)') -ForegroundColor Magenta
