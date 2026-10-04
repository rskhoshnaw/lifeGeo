# Use XmlReader with proper encoding
$reader = [System.Xml.XmlReader]::Create('f:\Projects\lifeGeo\temp_docx\word\document.xml')
$texts = @()
while ($reader.Read()) {
    if ($reader.NodeType -eq 'Element' -and $reader.LocalName -eq 't') {
        $text = $reader.ReadElementContentAsString()
        $texts += $text
    }
}
$reader.Close()
$fullText = $texts -join ' '
$fullText | Out-File -FilePath 'f:\Projects\lifeGeo\docx_text2.txt' -Encoding UTF8
Write-Host "Extracted $($texts.Count) text nodes"
Write-Host "First 2000 chars:"
Write-Host $fullText.Substring(0, [Math]::Min(2000, $fullText.Length))