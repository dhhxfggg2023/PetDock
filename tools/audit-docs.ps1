$ErrorActionPreference = 'Stop'
$dir = 'E:\projects\PetDock\docs'
$res = 'E:\projects\PetDock\research'
$exists = @{}
Get-ChildItem $dir -Filter '*.md' | ForEach-Object { $exists[$_.BaseName] = $true }

# D-04b conventions: ignore placeholder names (NN-xxx / NN-x) and strikethrough spans
# (~~path~~ means "this path does not exist / historical", allowed in prose).
function Strip-Allowed($text) {
    $t = $text -replace '~~[^~]*~~', ''
    return $t
}

$bad = New-Object System.Collections.ArrayList
Get-ChildItem $dir -Filter '*.md' | ForEach-Object {
    $raw = Strip-Allowed (Get-Content -LiteralPath $_.FullName -Raw -Encoding utf8)
    foreach ($m in [regex]::Matches($raw, '(docs|research)/([0-9A-Za-z_-]+)\.md')) {
        $kind = $m.Groups[1].Value
        $name = $m.Groups[2].Value
        if ($name -like 'NN-xxx' -or $name -like 'NN-x') { continue }
        if ($kind -eq 'docs') {
            if (-not $exists.ContainsKey($name)) {
                [void]$bad.Add(($_.BaseName + ' -> docs/' + $name + '.md'))
            }
        }
        else {
            $rp = Join-Path $res ($name + '.md')
            if (-not (Test-Path -LiteralPath $rp)) {
                [void]$bad.Add(($_.BaseName + ' -> research/' + $name + '.md'))
            }
        }
    }
}

function Count-Pat($pat) {
    # D-04b exemptions: ~~span~~ marks an explicitly historical / erroneous form, and a
    # line carrying a prohibition clause is the rule itself, not a violation.
    $lines = Get-ChildItem $dir -Filter '*.md' | Select-String -Pattern $pat -Encoding utf8
    $n = 0
    foreach ($l in $lines) {
        $txt = $l.Line
        if ($txt -match '~~') { continue }
        # exemption keywords written as \uXXXX so this script stays pure ASCII (PS 5.1 -File
        # reads a BOM-less file as ANSI and would mangle literal CJK into a dead regex).
        if ($txt -match '\u4e0d\u5b58\u5728|\u9ed1\u540d\u5355|\u7981\u6b62|\u4e0d\u5f97|\u7981\u7528|\u4fee\u590d\u524d|\u5df2\u6539|\u5df2\u4fee|superseded') { continue }
        $n++
    }
    return $n
}

if ($bad.Count -gt 0) {
    Write-Output ('DANGLING: ' + ($bad -join ' | '))
}
else {
    Write-Output 'AUDIT-OK no dangling docs/research refs'
}
Write-Output '--- docs file list ---'
Get-ChildItem $dir -File | Sort-Object Name | ForEach-Object { Write-Output ('  ' + $_.Name + '  ' + $_.Length) }
Write-Output ('docs md count: ' + (Get-ChildItem $dir -Filter '*.md').Count)
Write-Output ('research md count: ' + (Get-ChildItem $res -Filter '*.md').Count)
Write-Output ('Tier34 residual: ' + (Count-Pat 'Tier [34]'))
Write-Output ('AM11plus residual: ' + (Count-Pat 'AM-(1[1-9]|2[0-9])'))
Write-Output ('SM refs: ' + (Count-Pat 'SM-0[1-7]'))
Write-Output ('TBD placeholders: ' + (Count-Pat '\(TBD\)'))
Write-Output ('shimeji capability sample: ' + (Count-Pat 'assets\.read:shimeji'))
Write-Output ('D-04b present: ' + (Count-Pat 'D-04b'))
Write-Output 'END'
