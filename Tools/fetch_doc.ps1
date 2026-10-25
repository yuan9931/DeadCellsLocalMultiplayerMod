param([Parameter(Mandatory = $true)][string]$Url, [string]$OutFile)

$ErrorActionPreference = 'Stop'
$r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 60
$html = $r.Content

# Keep only the Docusaurus article body.
$start = $html.IndexOf('<article')
if ($start -lt 0) { $start = 0 }
$end = $html.IndexOf('</article>')
if ($end -lt 0) { $end = $html.Length }
$frag = $html.Substring($start, $end - $start)

# Drop scripts/styles, then convert some blocks to line breaks.
$frag = [regex]::Replace($frag, '(?is)<(script|style)[^>]*>.*?</(script|style)>', '')
$frag = [regex]::Replace($frag, '(?i)<br\s*/?>', "`n")
$frag = [regex]::Replace($frag, '(?i)</(p|div|li|h1|h2|h3|h4|pre|tr|table|blockquote|code-block)>', "`n")
$frag = [regex]::Replace($frag, '(?i)<pre[^>]*>', "`n---CODE---`n")
$frag = [regex]::Replace($frag, '(?i)</pre>', "`n---ENDCODE---`n")
# Strip remaining tags.
$frag = [regex]::Replace($frag, '(?s)<[^>]+>', '')
# Decode entities.
Add-Type -AssemblyName System.Web
$frag = [System.Web.HttpUtility]::HtmlDecode($frag)
# Collapse blank lines.
$lines = $frag -split "`n" | ForEach-Object { $_.TrimEnd() }
$out = New-Object System.Collections.Generic.List[string]
$blank = 0
foreach ($l in $lines) {
    if ($l -match '^\s*$') {
        $blank++
        if ($blank -le 1) { $out.Add('') }
    }
    else {
        $blank = 0
        $out.Add($l)
    }
}
$text = $out -join "`n"
if ($OutFile) {
    Set-Content -Path $OutFile -Value $text -Encoding UTF8
    "WROTE $OutFile ($($text.Length) chars)"
}
else {
    $text
}
