# Read the raw bytes and convert from UTF-8 bytes interpreted as Latin-1 to proper UTF-8
$bytes = [System.IO.File]::ReadAllBytes('f:\Projects\lifeGeo\docx_text.txt')
# The file contains UTF-8 bytes that were written as Latin-1, so we need to re-interpret
$text = [System.Text.Encoding]::UTF8.GetString($bytes)
$text | Out-File -FilePath 'f:\Projects\lifeGeo\docx_text_fixed.txt' -Encoding UTF8
Write-Host "Fixed text length: $($text.Length)"
Write-Host "First 2000 chars:"
Write-Host $text.Substring(0, [Math]::Min(2000, $text.Length))