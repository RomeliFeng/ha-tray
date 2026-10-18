# 下载 Material Design Icons 字体（HA 的图标就是它），生成「图标名 → 字符」对照表。
# 产物放在 src\HaTray\Assets，提交进仓库：
#   mdi.ttf               字体
#   mdi-codepoints.txt    每行「名字 十六进制码位」，比如「lightbulb F0335」
#   mdi-LICENSE.txt       许可（字体 Apache-2.0）
# 升级字体时改 $Version 重跑。
param([string]$Version = '7.4.47')

$ErrorActionPreference = 'Stop'
$base = "https://cdn.jsdelivr.net/npm/@mdi/font@$Version"
$assets = Join-Path $PSScriptRoot '..\src\HaTray\Assets'

Invoke-WebRequest "$base/fonts/materialdesignicons-webfont.ttf" -OutFile (Join-Path $assets 'mdi.ttf')
Invoke-WebRequest "$base/LICENSE" -OutFile (Join-Path $assets 'mdi-LICENSE.txt')

# 样式表里每个图标是一条 .mdi-名字::before{content:"\F0335"}，只拿来生成对照表，不保存。
$css = (Invoke-WebRequest "$base/css/materialdesignicons.min.css").Content
$lines = [regex]::Matches($css, '\.mdi-([a-z0-9-]+)::before\{content:"\\([0-9A-F]+)"\}') |
    ForEach-Object { "$($_.Groups[1].Value) $($_.Groups[2].Value)" }
if ($lines.Count -lt 5000) { throw "只解析出 $($lines.Count) 个图标，样式表格式可能变了" }
Set-Content (Join-Path $assets 'mdi-codepoints.txt') ($lines -join "`n") -NoNewline -Encoding utf8NoBOM
"MDI $Version：$($lines.Count) 个图标"
