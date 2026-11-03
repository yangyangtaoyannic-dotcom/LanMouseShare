# Bulk upload dist versions to GitHub Releases
# 1. Generate token at https://github.com/settings/tokens/new (classic, check 'repo')
# 2. Run: Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
# 3. Run: .\Upload-Releases.ps1

$TOKEN = Read-Host -Prompt "Enter GitHub Personal Access Token"
$OWNER = "yangyangtaoyannic-dotcom"
$REPO  = "LanMouseShare"
$DIST  = "C:\Users\yangyang\Documents\局域网键盘鼠标共享\dist"

$headers = @{
    Authorization = "token $TOKEN"
    Accept        = "application/vnd.github+json"
}

# Fetch existing releases to skip duplicates
try {
    $existing = Invoke-RestMethod -Uri "https://api.github.com/repos/$OWNER/$REPO/releases?per_page=100" -Headers $headers -Method GET
    $existingTags = $existing | ForEach-Object { $_.tag_name }
} catch {
    Write-Error "Failed to fetch existing releases. Check your token and network."
    exit 1
}

$versionDirs = Get-ChildItem -Path $DIST -Directory | Sort-Object Name

foreach ($dir in $versionDirs) {
    $folderName = $dir.Name
    $version    = $folderName -replace '^LanMouseShare-', ''
    $tag        = "v$version"

    if ($existingTags -contains $tag) {
        Write-Host "[$tag] Already exists, skipping." -ForegroundColor Yellow
        continue
    }

    Write-Host "[$tag] Creating release..." -ForegroundColor Cyan -NoNewline

    # Create release
    $body = @{
        tag_name = $tag
        name     = "LanMouseShare $version"
        body     = "Release $version"
    } | ConvertTo-Json -Depth 3

    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$OWNER/$REPO/releases" `
            -Headers $headers -Method POST -Body $body -ContentType "application/json"
        Write-Host " OK" -ForegroundColor Green
    } catch {
        Write-Host " FAILED: $_" -ForegroundColor Red
        continue
    }

    $uploadUrl = $release.upload_url -replace '\{\?name,label\}', ''

    # Collect files to upload
    $files = @()

    $sourceZip = Get-ChildItem -Path $dir.FullName -Filter "*source.zip" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($sourceZip) { $files += $sourceZip }

    $setupExe = Get-ChildItem -Path $dir.FullName -Filter "*Setup.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($setupExe) { $files += $setupExe }

    # If no specific files, zip the whole directory
    if ($files.Count -eq 0) {
        $zipPath = Join-Path $env:TEMP "$folderName.zip"
        Compress-Archive -Path "$($dir.FullName)\*" -DestinationPath $zipPath -Force
        $files += Get-Item $zipPath
    }

    foreach ($file in $files) {
        $fileName = [System.Uri]::EscapeDataString($file.Name)
        $sizeMB   = [math]::Round($file.Length / 1MB, 1)
        Write-Host "  Uploading $($file.Name) ($sizeMB MB)..." -NoNewline

        try {
            Invoke-RestMethod -Uri "$uploadUrl?name=$fileName" `
                -Headers @{ Authorization = "token $TOKEN"; "Content-Type" = "application/octet-stream" } `
                -Method POST -InFile $file.FullName | Out-Null
            Write-Host " OK" -ForegroundColor Green
        } catch {
            Write-Host " FAILED: $_" -ForegroundColor Red
        }
    }

    Start-Sleep -Seconds 1
}

Write-Host "`nDone! Visit https://github.com/$OWNER/$REPO/releases" -ForegroundColor Green
