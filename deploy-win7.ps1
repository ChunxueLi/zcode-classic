# ZCode7 Win7 deploy script (usage: deploy-win7.ps1 [-Watch])
# Target: <WIN7-IP> (Win7 SP1 credentials via env or edit)
param(
    [switch]$Watch,
    [switch]$Launch
)

$target = "<WIN7-IP>"
$user = "<USER>"
$pass = "<PASSWORD>"
$src = "D:\work\aidev\zcode7\out"
$remoteDir = "\\<WIN7-IP>\C$\ZCode7"
$log = "D:\work\aidev\zcode7\win7_watch.log"

$files = @("ZCode7.exe", "WebView2Loader.dll", "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll")

function Connect-Share {
    net use "\\<WIN7-IP>\C$" /delete /y 2>$null | Out-Null
    net use "\\<WIN7-IP>\C$" /user:$user $pass /persistent:no | Out-Null
    return ($LASTEXITCODE -eq 0)
}

function Deploy-Once {
    Write-Host "connecting..."
    if (-not (Connect-Share)) { Write-Host "connect failed"; return $false }
    if (-not (Test-Path $remoteDir)) { New-Item -ItemType Directory -Path $remoteDir -Force | Out-Null }
    foreach ($f in $files) {
        if (Test-Path "$src\$f") {
            Copy-Item "$src\$f" "$remoteDir\$f" -Force
            Write-Host "  copied $f"
        }
    }
    if (Test-Path "$src\web2") {
        robocopy "$src\web2" "$remoteDir\web2" /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
        Write-Host "  synced web2"
    }
    net use "\\<WIN7-IP>\C$" /delete /y | Out-Null
    Write-Host "deploy done"
    return $true
}

if ($Watch) {
    "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] watch started, probing $target every 60s" | Out-File $log -Encoding utf8
    while ($true) {
        $up = Test-Connection -ComputerName $target -Count 1 -Quiet -ErrorAction SilentlyContinue
        if ($up) {
            "[$(Get-Date -Format 'HH:mm:ss')] $target online, deploying" | Out-File $log -Append -Encoding utf8
            if (Deploy-Once) {
                "[$(Get-Date -Format 'HH:mm:ss')] deploy OK, watch end" | Out-File $log -Append -Encoding utf8
                break
            }
        }
        Start-Sleep -Seconds 60
    }
} else {
    $r = Deploy-Once
    if (-not $r) { exit 1 }
}
