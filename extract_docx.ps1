[xml]$xml = Get-Content 'f:\Projects\lifeGeo\temp_docx\word\document.xml' -Raw
$nsm = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
$nsm.AddNamespace('w', 'http://schemas.openxmlformats.org/wordprocessingml/2006/main')
$texts = $xml.SelectNodes('//w:t', $nsm) | ForEach-Object { $_.InnerText }
$fullText = $texts -join ' '
$fullText | Out-File -FilePath 'f:\Projects\lifeGeo\docx_text.txt' -Encoding UTF8
Write-Host "Extracted $( $texts.Count ) text nodes"
Write-Host "First 1000 chars: $( $fullText.Substring(0, [Math]::Min(1000, $fullText.Length)) )"