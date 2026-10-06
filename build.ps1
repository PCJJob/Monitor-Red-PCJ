# build.ps1 — Compila Monitor de Red PCJ y genera el instalador.
#
# Uso:  powershell -ExecutionPolicy Bypass -File build.ps1
#
# Busca el SDK de .NET y el compilador de Inno Setup en los sitios habituales; si no los
# encuentra, dice qué instalar y desde dónde. No depende de la carpeta de ningún usuario.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Tiene-SDK {
    param($exe)
    try {
        $lista = & $exe --list-sdks 2>$null
        return (@($lista | Where-Object { $_ -match '\d' }).Count -gt 0)
    } catch { return $false }
}

function Busca-Dotnet {
    # Importa el orden y el filtro: en una máquina puede haber un dotnet en el PATH que solo
    # trae el runtime (no sabe hacer «publish») y el SDK instalado en otro sitio. Se prueba
    # cada candidato y se queda con el primero que de verdad tenga un SDK.
    $candidatos = @()
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidatos += $cmd.Source }
    $candidatos += "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
    $candidatos += "$env:ProgramFiles\dotnet\dotnet.exe"
    $candidatos += "${env:ProgramFiles(x86)}\dotnet\dotnet.exe"
    foreach ($c in $candidatos) {
        if ($c -and (Test-Path $c) -and (Tiene-SDK $c)) { return $c }
    }
    throw "No se encuentra un dotnet con SDK de .NET 8. Instala el SDK desde https://dotnet.microsoft.com/download/dotnet/8.0 (el runtime solo no vale: hace falta «dotnet publish»)."
}

function Busca-Inno {
    foreach ($c in @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $c) { return $c }
    }
    throw "No se encuentra Inno Setup 6 (ISCC.exe). Descargalo de https://jrsoftware.org/isdl.php"
}

$dotnet = Busca-Dotnet
$iscc   = Busca-Inno
Write-Host "dotnet: $dotnet"
Write-Host "ISCC  : $iscc"

Write-Host '== Generando icono =='
& powershell -ExecutionPolicy Bypass -File "$root\tools\gen-icon.ps1"

Write-Host '== Publicando app (self-contained win-x64) =='
& $dotnet publish "$root\src\MonitorRedPCJ" -c Release -r win-x64 --self-contained true -o "$root\dist\app"
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: «dotnet publish» falló (código $LASTEXITCODE). Suele ser una copia de MonitorRedPCJ abierta que bloquea dist\app: ciérrala y reintenta." -ForegroundColor Red
    throw "dotnet publish falló"
}

# Confirmar que el binario publicado es realmente nuevo, no un resto bloqueado.
$pub = Get-Item "$root\dist\app\MonitorRedPCJ.dll"
Write-Host ("Publicado MonitorRedPCJ.dll  version={0}  fecha={1}" -f $pub.VersionInfo.ProductVersion, $pub.LastWriteTime)

Write-Host '== Compilando instalador =='
& $iscc "$root\installer\setup.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC falló (código $LASTEXITCODE)" }

$exe = Get-ChildItem "$root\dist\MonitorRedPCJ-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Copy-Item $exe.FullName "$root\$($exe.Name)" -Force
Write-Host "== LISTO: $root\$($exe.Name) =="
