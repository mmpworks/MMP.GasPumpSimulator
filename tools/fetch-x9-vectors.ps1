# Downloads the ANSI X9.24-3-2017 AES DUKPT test vectors (free from x9.org) for anchor test A1.
#
# The PDF and its text stay in tests/GasPump.Tests/x9/, which git ignores: the document is
# copyright ASC X9. The SHA-256 below is the one MMP.NetworkSecurity locks for the same file
# (acceptance/vectors/sources.lock.json). A different hash stops the script.
#
# Needs pdftotext (poppler) on PATH. Git for Windows ships it in mingw64/bin.
# After this script, `dotnet test` also runs the second A1 test: every value in the committed file
# tests/GasPump.Tests/Anchors/x9-24-3-aes-dukpt.vectors equals this text in its place.
# tools/extract-x9-vectors.py writes that file from this text.
$ErrorActionPreference = 'Stop'
$url = 'https://x9.org/wp-content/uploads/2018/03/X9.24-3-2017-Test-Vectors-20180129-1.pdf'
$expected = 'AE9BB01D76EDA32018332F1202A9C2010097ABF0D17D753612B7E612DDD9C4CC'
$dir = Join-Path $PSScriptRoot '..\tests\GasPump.Tests\x9'
$pdf = Join-Path $dir 'X9.24-3-2017-Test-Vectors-20180129-1.pdf'
$txt = Join-Path $dir 'X9.24-3-2017-Test-Vectors.txt'

New-Item -ItemType Directory -Force -Path $dir | Out-Null
if (-not (Test-Path $pdf)) { Invoke-WebRequest -Uri $url -OutFile $pdf }
$actual = (Get-FileHash $pdf -Algorithm SHA256).Hash
if ($actual -ne $expected) {
    Remove-Item $txt -ErrorAction SilentlyContinue
    throw "SOURCE_CHANGED: $pdf has SHA-256 $actual, expected $expected. Read the new document before you change the hash."
}
& pdftotext $pdf $txt
if ($LASTEXITCODE -ne 0) { throw "pdftotext failed with exit code $LASTEXITCODE" }
Write-Host "X9.24-3 vectors ready: $txt"
