# 推送到 GitHub 之前查个人资料：当前文件、全部提交、提交说明、提交者身份。
# 关键词清单不进仓库（清单里的房间名、内网地址本身就是个人资料），放在本机：
#   %LOCALAPPDATA%\HaTray\privacy-patterns.txt   每行一个正则（不区分大小写），# 开头是注释
# 没查到返回 0，查到或清单不存在返回 1。
#   pwsh tools\check-privacy.ps1
$ErrorActionPreference = 'Stop'
$repo = Join-Path $PSScriptRoot '..'
$list = Join-Path $env:LOCALAPPDATA 'HaTray\privacy-patterns.txt'
if (-not (Test-Path $list)) {
    Write-Output "没有关键词清单：$list"
    exit 1
}

$patterns = Get-Content $list -Encoding utf8 | Where-Object { $_ -and -not $_.StartsWith('#') }
$regex = ($patterns -join '|')
$skip = ':!src/HaTray/Assets/mdi-codepoints.txt'   # 图标名对照表，七千多个英文单词，只会误报
$hits = 0

Push-Location $repo
try {
    Write-Output '== 当前文件'
    $now = git grep -n -I -i -E $regex -- . $skip
    $now | ForEach-Object { Write-Output "  $_" }
    $hits += @($now).Count

    Write-Output '== 历史提交里的文件（按「文件:命中」去重）'
    $old = foreach ($c in git rev-list --all) { git grep -I -i -E -o $regex $c -- . $skip 2>$null }
    $old = $old | ForEach-Object { $_ -replace '^[0-9a-f]+:', '' } | Sort-Object -Unique
    $old | ForEach-Object { Write-Output "  $_" }
    $hits += @($old).Count

    Write-Output '== 提交说明'
    $msgs = git log --all --format='%h %s%n%b' | Select-String -Pattern $regex
    $msgs | ForEach-Object { Write-Output "  $($_.Line)" }
    $hits += @($msgs).Count

    # 提交者邮箱应该是 GitHub 的匿名邮箱（…@users.noreply.github.com），其他邮箱推上去就公开了。
    Write-Output '== 提交者'
    $ids = git log --all --format='%an <%ae>%n%cn <%ce>' | Sort-Object -Unique
    foreach ($id in $ids) {
        $bad = $id -notmatch '@users\.noreply\.github\.com>$'
        Write-Output ("  {0}{1}" -f $id, $(if ($bad) { '   ← 不是匿名邮箱' } else { '' }))
        if ($bad) { $hits++ }
    }
}
finally {
    Pop-Location
}

Write-Output ''
if ($hits -gt 0) {
    Write-Output "查到 $hits 处，推送前先处理。"
    exit 1
}

Write-Output '没有查到个人资料。'
exit 0
