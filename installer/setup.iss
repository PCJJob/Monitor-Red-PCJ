; Instalador de "Monitor de Red PCJ" — software libre con licencia MIT, sin pagos.
; Compilar con ISCC.exe desde la carpeta raíz del proyecto.

#define MyAppName "Monitor de Red PCJ"
#define MyAppVersion "1.6.9"
#define MyAppPublisher "PCJ"
#define MyAppExeName "MonitorRedPCJ.exe"

[Setup]
AppId={{7B9E2F4C-5A31-4D8E-9C60-1F2E3D4C5B6A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (c) 2026 PCJ — Monitor de Red PCJ
; La pantalla de licencia del asistente: LICENSE está en la raíz del proyecto (esta carpeta es
; installer/). Es solo una página más al principio del flujo; no cambia lo demás.
LicenseFile=../LICENSE
DefaultDirName={autopf}\MonitorRedPCJ
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=../dist
OutputBaseFilename=MonitorRedPCJ-Setup-{#MyAppVersion}
SetupIconFile=../src/MonitorRedPCJ/assets/app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear acceso directo en el escritorio"; GroupDescription: "Accesos directos:"
Name: "startup"; Description: "Ejecutar {#MyAppName} al iniciar Windows"; GroupDescription: "Arranque:"

[Files]
Source: "../dist/app/*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "register-task.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Tarea "startup": clave Run del usuario actual
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MonitorRedPCJ"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; Tasks: startup; Flags: uninsdeletevalue

[Run]
; Registrar la tarea de arranque elevado (el instalador ya corre como admin, así que no
; vuelve a pedir UAC). Sin disparador: solo se lanza a demanda para elevar el .exe.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\register-task.ps1"" ""{app}\{#MyAppExeName}"""; Flags: runhidden; StatusMsg: "Preparando el arranque como administrador…"
Filename: "{app}\{#MyAppExeName}"; Description: "Ejecutar {#MyAppName} ahora"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Al desinstalar, quitar nuestras reglas del firewall y la tarea programada
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=all group=""Monitor de Red PCJ"""; Flags: runhidden; RunOnceId: "DelFwRules"
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""MonitorRedPCJ"" /F"; Flags: runhidden; RunOnceId: "DelTask"

[Code]
// Antes de copiar archivos hay que terminar la app. Como la "X" la manda a la bandeja
// en vez de cerrarla, el cierre normal del instalador no la detiene y deja el .exe
// bloqueado (quedaba la versión vieja instalada). El instalador va elevado, así que
// taskkill /F sí puede matar incluso una copia elevada en la bandeja.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  R, i: Integer;
  exePath: String;
begin
  exePath := ExpandConstant('{app}\MonitorRedPCJ.exe');
  // Matar la app y repetir hasta que ya no quede ningún proceso (taskkill devuelve 128
  // cuando no encuentra ninguna instancia). Con Sleep corto basta para que el sistema
  // libere el manejador del .exe antes de que empiece la copia de archivos.
  for i := 1 to 10 do
  begin
    Exec('taskkill.exe', '/F /FI "IMAGENAME eq MonitorRedPCJ.exe"', '', SW_HIDE, ewWaitUntilTerminated, R);
    Sleep(1000);
    if not FileExists(exePath) or (R = 128) then
    begin
      // Sin instancias vivas: dar un margen extra para la liberación del handle.
      Sleep(1500);
      Break;
    end;
  end;
  Result := '';
end;

// Limpieza de datos del usuario al desinstalar (con confirmación)
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  R: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    R := MsgBox('¿También quieres borrar los datos y el histórico de Monitor de Red PCJ?',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2);
    if R = IDYES then
      DelTree(ExpandConstant('{userappdata}\MonitorRedPCJ'), True, True, True);
  end;
end;
