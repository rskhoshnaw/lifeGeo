$path = 'f:\Projects\lifeGeo\docs\سند نیازمندی محصول LifeDrop.docx'
if (Test-Path -LiteralPath $path) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    $entry = $zip.GetEntry('word/document.xml')
    if ($entry) {
        $stream = $entry.Open()
        $reader = [System.Xml.XmlReader]::Create($stream)
        $text = ''
        while ($reader.Read()) {
            if ($reader.NodeType -eq 'Text') {
                $text += $reader.Value + ' '
            }
        }
        $reader.Close()
        $stream.Close()
        $zip.Close()
        Write-Output $text
    } else {
        Write-Output 'word/document.xml not found in docx'
    }
} else {
    Write-Output 'File not found'
}