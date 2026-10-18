# 生成 README 用的截图：docs\images\<语言>-<页面>.png。
# 用演示模式（--demo，数据是编的，不会带出真实家庭的信息），中英两种界面各截一套。
# 面板背后先铺一块全屏纯色窗口，免得半透明的面板透出桌面。
# 只能有一个 HaTray 在跑：正在跑的会先关掉，截完再按原来的参数重新打开。
#   powershell -ExecutionPolicy Bypass -File tools\make-screenshots.ps1
# 要先构建：dotnet build src\HaTray -c Debug -p:Platform=x64
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -Name Dpi -Namespace HaTrayShots -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(System.IntPtr value);'
[HaTrayShots.Dpi]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null   # 按物理像素截，和面板窗口的坐标一致

$root = Join-Path $PSScriptRoot '..'
$exe = Get-ChildItem (Join-Path $root 'src\HaTray\bin\x64\Debug') -Recurse -Filter HaTray.exe | Select-Object -First 1
if (-not $exe) { throw '没找到 HaTray.exe，先构建' }
$out = Join-Path $root 'docs\images'
New-Item -ItemType Directory -Force $out | Out-Null
$data = Join-Path $env:LOCALAPPDATA 'HaTray'
$cmdFile = Join-Path $data 'debug-cmd.txt'
$resultFile = Join-Path $data 'debug-result.json'

# 记下正在跑的 HaTray 的参数，截完照原样打开。
$running = Get-CimInstance Win32_Process -Filter "Name = 'HaTray.exe'" | Select-Object -First 1
$restart = if ($running) { ($running.CommandLine -replace '^\s*("[^"]+"|\S+)\s*', '') } else { $null }
Get-Process HaTray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

# 纯色背景：盖住所有屏幕，颜色是中性灰。
$backdrop = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', @'
Add-Type -AssemblyName System.Windows.Forms
$f = New-Object Windows.Forms.Form
$f.FormBorderStyle = 'None'; $f.StartPosition = 'Manual'; $f.ShowInTaskbar = $false
$f.Bounds = [Windows.Forms.SystemInformation]::VirtualScreen
$f.BackColor = [Drawing.Color]::FromArgb(58, 58, 60)
[Windows.Forms.Application]::Run($f)
'@
Start-Sleep -Seconds 2

function Send([string]$cmd, [int]$wait = 1200) {
    Set-Content $cmdFile $cmd -Encoding utf8
    Start-Sleep -Milliseconds $wait
    Get-Content $resultFile -Raw -Encoding utf8 | ConvertFrom-Json
}

function Shot([string]$name) {
    $r = Send 'dump'
    $w = $r.window
    $bmp = New-Object System.Drawing.Bitmap $w.w, $w.h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($w.x, $w.y, 0, 0, $bmp.Size)
    $g.Dispose()

    # 窗口是系统画的 8 DIP 圆角，圆角外面截到的是背景色：改成透明。
    $radius = [int][math]::Round(8 * $w.w / 400)
    for ($dy = 0; $dy -lt $radius; $dy++) {
        for ($dx = 0; $dx -lt $radius; $dx++) {
            $ox = $radius - $dx - 0.5; $oy = $radius - $dy - 0.5
            if ($ox * $ox + $oy * $oy -gt $radius * $radius) {
                $right = $w.w - 1 - $dx
                $bottom = $w.h - 1 - $dy
                foreach ($p in @(@($dx, $dy), @($right, $dy), @($dx, $bottom), @($right, $bottom))) {
                    $bmp.SetPixel($p[0], $p[1], [System.Drawing.Color]::Transparent)
                }
            }
        }
    }

    $path = Join-Path $out "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output ("{0}  {1}×{2}  {3}" -f $name, $w.w, $w.h, $r.page.page)
}

try {
    foreach ($lang in 'en', 'zh') {
        $app = Start-Process $exe.FullName -PassThru -ArgumentList '--demo', '--debug', "--lang=$lang"
        Start-Sleep -Seconds 5
        Send 'show' | Out-Null
        Send 'room living' | Out-Null
        Shot "$lang-main"
        Send 'detail climate.living_ac' | Out-Null
        Shot "$lang-detail"
        Send 'back' | Out-Null
        Send 'edit' 1600 | Out-Null
        Shot "$lang-edit"
        Send 'done' | Out-Null
        Stop-Process -Id $app.Id -Force
        Start-Sleep -Seconds 1
    }
}
finally {
    Stop-Process -Id $backdrop.Id -Force -ErrorAction SilentlyContinue
    Get-Process HaTray -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($null -ne $restart) {
        Start-Sleep -Seconds 1
        if ($restart.Length -gt 0) { Start-Process $exe.FullName -ArgumentList $restart } else { Start-Process $exe.FullName }
    }
}
