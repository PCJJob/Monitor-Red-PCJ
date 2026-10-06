# comments-xaml.ps1 — Revisa que ningún comentario XAML lleve guiones seguidos dentro.
# La regla de XML es que adentro de <!-- --> no puede aparecer "--", así que los separadores
# de raya larga (------- título -------) rompen la compilación con un error que no dice dónde.
# Uso:  powershell -ExecutionPolicy Bypass -File comments-xaml.ps1
$ErrorActionPreference = 'Stop'
$raiz = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\src\MonitorRedPCJ'
$sucio = 0
Get-ChildItem $raiz -Recurse -Filter *.xaml | ForEach-Object {
    $texto = Get-Content -Raw -Encoding UTF8 $_.FullName
    for ($i = 0; $i -lt $texto.Length - 5; $i++) {
        if ($texto[$i] -ne '<') { continue }
        if ($texto.Substring($i, [Math]::Min(4, $texto.Length - $i)) -ne '<!--') { continue }
        $fin = $texto.IndexOf('-->', $i + 4)
        if ($fin -lt 0) { break }
        $interior = $texto.Substring($i + 4, $fin - $i - 4)
        if ($interior.Contains('--')) {
            $sucio++
            $linea = ($texto.Substring(0, $i) -split "`n").Count
            Write-Host ("MAL  {0}:{1}  {2}" -f $_.Name, $linea, ($interior.Trim().Substring(0, [Math]::Min(60, $interior.Trim().Length))))
        }
        $i = $fin + 2
    }
}
if ($sucio -eq 0) { Write-Host 'Comentarios correctos en todo el XAML.' }
else { Write-Host "$sucio comentario(s) con guiones seguidos." }
