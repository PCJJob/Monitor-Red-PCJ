using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MonitorRedPCJ.Services.Tools
{
    /// Lo que se sabe de un ejecutable: si está firmado, por quién y si la firma es sana.
    public class FirmaInfo
    {
        public string Exe { get; set; } = "";
        public string App { get; set; } = "";
        public string Editor { get; set; } = "";       // CompanyName del propio fichero
        public string Firmante { get; set; } = "";     // CN del certificado de firma
        public string Estado { get; set; } = "";       // valida | sin_firmar | dudosa | ausente | rar
        public string Detalle { get; set; } = "";
        public bool Sospechosa { get; set; }          // firma que no cuadra con la ruta o con el nombre
        public long Bytes { get; set; }
    }

    /// Fila tal como se guarda en signature.json. Se compara tamaño + fecha de modificación:
    /// si el ejecutable no cambió, no se vuelve a verificar.
    internal class FilaFirma
    {
        public long tam { get; set; }
        public long mod { get; set; }
        public string editor { get; set; } = "";
        public string firmante { get; set; } = "";
        public string estado { get; set; } = "";
        public string detalle { get; set; } = "";
    }

    /// Identidad de las apps: firma digital Authenticode de cada ejecutable que PCJ conoce.
    /// Es lo que permite distinguir «chrome.exe» de «chrome.exe firmado por Google LLC».
    ///
    /// La verificación la hace Windows a través de Get-AuthenticodeSignature (se lanza
    /// PowerShell por lotes: ocho ficheros en unos 120 ms, y solo cuando algo cambió). Hacer
    /// el WinVerifyTrust a mano con P/Invoke exige replicar una estructura de catorce campos
    /// y un unions; un error ahí da por válida una firma que no lo es, que es justo lo
    /// contrario de lo que quiere esta herramienta.
    public class SignatureService
    {
        public static SignatureService? Instancia { get; private set; }

        public event Action? Actualizado;

        private readonly object _lock = new object();
        private readonly Dictionary<string, FilaFirma> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FirmaInfo> _listo = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _fichero;
        private Timer? _timer;
        private int _viendo;
        private volatile bool _trabajando;
        private List<string> _porVer = new();
        private const int PorPasada = 8;

        public SignatureService()
        {
            _fichero = Path.Combine(Paths.Root, "signature.json");
            Instancia = this;
            Cargar();
        }

        public void Start()
        {
            if (_timer != null) return;
            _trabajando = true;
            Reencolar();
            _timer = new Timer(_ => Vuelta(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4));
        }

        public void Stop()
        {
            _trabajando = false;
            var t = Interlocked.Exchange(ref _timer, null);
            try { t?.Dispose(); } catch { }
            Guardar();
        }

        public bool Trabajando => _trabajando;

        /// Fuerza a repasarlo todo otra vez (el botón «Repasar ahora»).
        public void RepasarAhora()
        {
            lock (_lock) { _cache.Clear(); _listo.Clear(); }
            Reencolar();
            Vuelta();
        }

        private void Reencolar()
        {
            var exes = AppsConocidas();
            lock (_lock) _porVer = exes.Where(e => !_listo.ContainsKey(e)).ToList();
        }

        /// Todas las rutas de ejecutable que PCJ tiene en su histórico o ahora en ejecución.
        public static List<string> AppsConocidas()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var a in App.Traffic.GetApps())
                    if (!string.IsNullOrWhiteSpace(a.ExePath)) set.Add(a.ExePath);
            }
            catch { }
            try
            {
                foreach (var e in App.Traffic.GetRunningExes())
                    if (!string.IsNullOrWhiteSpace(e)) set.Add(e);
            }
            catch { }
            return set.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void Vuelta()
        {
            if (!_trabajando || App.IsShuttingDown) return;
            if (Interlocked.CompareExchange(ref _viendo, 1, 0) != 0) return;
            try
            {
                List<string> lote;
                lock (_lock)
                {
                    lote = _porVer.Take(PorPasada).ToList();
                    _porVer = _porVer.Skip(lote.Count).ToList();
                }
                if (lote.Count == 0) return;

                var resultados = AnalizarLote(lote);
                lock (_lock)
                {
                    foreach (var f in resultados)
                    {
                        _listo[f.Exe] = f;
                        _cache[f.Exe] = new FilaFirma
                        {
                            tam = f.Bytes,
                            mod = TicksDe(f.Exe),
                            editor = f.Editor,
                            firmante = f.Firmante,
                            estado = f.Estado,
                            detalle = f.Detalle,
                        };
                    }
                }
                Guardar();
                Actualizado?.Invoke();
            }
            catch { }
            finally { Interlocked.Exchange(ref _viendo, 0); }
        }

        private static long TicksDe(string exe)
        {
            try { return File.GetLastWriteTimeUtc(exe).Ticks; } catch { return 0; }
        }

        // ---------- Verificación por lotes ----------

        private List<FirmaInfo> AnalizarLote(List<string> lote)
        {
            var salida = new List<FirmaInfo>();
            var pendientes = new List<string>();
            var datos = new Dictionary<string, FilaFirma>(StringComparer.OrdinalIgnoreCase);

            foreach (var exe in lote)
            {
                var info = new FirmaInfo
                {
                    Exe = exe,
                    App = Path.GetFileName(exe),
                };
                try { info.Bytes = new FileInfo(exe).Length; } catch { }

                if (!File.Exists(exe))
                {
                    info.Estado = "ausente";
                    info.Detalle = "El fichero ya no está en esa ruta.";
                    salida.Add(info);
                    continue;
                }

                lock (_lock)
                {
                    if (_cache.TryGetValue(exe, out var c) && c != null &&
                        c.tam == info.Bytes && c.mod == TicksDe(exe) &&
                        !string.IsNullOrEmpty(c.estado))
                    {
                        info.Editor = c.editor;
                        info.Firmante = c.firmante;
                        info.Estado = c.estado;
                        info.Detalle = c.detalle;
                        salida.Add(Finalizar(info));
                        continue;
                    }
                }
                pendientes.Add(exe);
                datos[exe] = new FilaFirma();
            }

            if (pendientes.Count == 0) return salida;

            var firmadas = Verificar(pendientes);
            foreach (var exe in pendientes)
            {
                var info = new FirmaInfo { Exe = exe, App = Path.GetFileName(exe) };
                try { info.Bytes = new FileInfo(exe).Length; } catch { }
                try
                {
                    var v = FileVersionInfo.GetVersionInfo(exe);
                    info.Editor = (v.CompanyName ?? "").Trim();
                }
                catch { }
                if (!firmadas.TryGetValue(exe, out var r)) r = ("rar", "", "Windows no devolvió estado para este fichero.");
                info.Estado = r.Item1;
                info.Firmante = r.Item2;
                info.Detalle = r.Item3;
                salida.Add(Finalizar(info));
            }
            return salida;
        }

        /// Ejecuta Get-AuthenticodeSignature sobre la lista y traduce el resultado.
        private static Dictionary<string, (string estado, string firmante, string detalle)> Verificar(List<string> archivos)
        {
            var tabla = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string lista = JsonSerializer.Serialize(archivos);
                const string cmd =
                    "$l=[Console]::In.ReadToEnd()|ConvertFrom-Json;" +
                    "$o=foreach($f in $l){try{$s=Get-AuthenticodeSignature -FilePath $f;" +
                    "[pscustomobject]@{p=$f;st=[string]$s.Status;ms=[string]$s.StatusMessage;" +
                    "cn=if($s.SignerCertificate){[string]$s.SignerCertificate.Subject}else{''};" +
                    "na=if($s.SignerCertificate){$s.SignerCertificate.NotAfter.ToString('o')}else{''}" +
                    "}}catch{[pscustomobject]@{p=$f;st='Error';ms=$_.Exception.Message;cn='';na=''}}};" +
                    "ConvertTo-Json -InputObject @($o) -Compress";

                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                            "WindowsPowerShell", "v1.0", "powershell.exe"),
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + cmd + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false),
                    StandardOutputEncoding = new UTF8Encoding(false),
                };
                using var p = Process.Start(psi);
                if (p == null) return tabla;
                p.StandardInput.Write(lista);
                p.StandardInput.Close();
                string json = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(25000)) { try { p.Kill(); } catch { } }
                if (string.IsNullOrWhiteSpace(json)) return tabla;

                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string ruta = Texto(el, "p");
                    if (string.IsNullOrEmpty(ruta)) continue;
                    string st = Texto(el, "st");
                    string cn = SacarCN(Texto(el, "cn"));
                    string msg = Texto(el, "ms");
                    DateTime hasta = default;
                    var na = Texto(el, "na");
                    if (!string.IsNullOrEmpty(na)) DateTime.TryParse(na, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal, out hasta);
                    tabla[ruta] = Traducir(st, cn, msg, hasta);
                }
            }
            catch { }
            return tabla;
        }

        private static string Texto(JsonElement e, string propiedad)
        {
            try
            {
                if (!e.TryGetProperty(propiedad, out var v)) return "";
                return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
            }
            catch { return ""; }
        }

        private static (string, string, string) Traducir(string st, string cn, string msg, DateTime hasta)
        {
            string firmante = cn;
            switch (st.ToLowerInvariant())
            {
                case "valid":
                    if (hasta != default && hasta < DateTime.Now)
                        return ("valida", firmante, "Firmado por " + Nombrar(firmante) +
                            ". El certificado caducó el " + hasta.ToString("dd/MM/yyyy") +
                            ", pero la marca de tiempo mantiene la firma en pie.");
                    return ("valida", firmante, "Firmado por " + Nombrar(firmante) + ". Windows valida la firma.");
                case "notsigned":
                    return ("sin_firmar", "", "El ejecutable no lleva firma digital Authenticode.");
                case "hashmismatch":
                    return ("dudosa", firmante, "La firma no corresponde con el contenido: el fichero fue " +
                        "modificado después de firmarse (o está corrupto).");
                case "nottrusted":
                    return ("dudosa", firmante, "Firmado por " + Nombrar(firmante) +
                        ", pero el certificado no es de una entidad de confianza: " + msg);
                case "errortype":
                    return ("rar", "", "Windows no reconoce este tipo de fichero como firmable.");
                default:
                    return ("rar", firmante, msg.Length > 0 ? msg : "Windows no devolvió un estado claro.");
            }
        }

        private static string Nombrar(string firmante) =>
            string.IsNullOrEmpty(firmante) ? "un certificado propio" : firmante;

        private static string SacarCN(string sujeto)
        {
            foreach (var parte in sujeto.Split(','))
            {
                var kv = parte.Split(new[] { '=' }, 2);
                if (kv.Length == 2 && kv[0].Trim().Equals("CN", StringComparison.OrdinalIgnoreCase))
                    return kv[1].Trim();
            }
            return sujeto.Trim();
        }

        // ---------- Qué es sospechoso ----------

        private static readonly HashSet<string> NombresPrestigiosos = new(StringComparer.OrdinalIgnoreCase)
        {
            "svchost.exe", "lsass.exe", "chrome.exe", "msedge.exe", "firefox.exe",
            "explorer.exe", "taskhostw.exe", "dllhost.exe", "conhost.exe", "services.exe",
            "wininit.exe", "csrss.exe", "teams.exe", "discord.exe", "opera.exe", "brave.exe",
        };

        private static FirmaInfo Finalizar(FirmaInfo info)
        {
            info.Sospechosa = EsSospechoso(info.Exe, info);
            if (info.Sospechosa)
            {
                string raro = RutaRara(info.Exe);
                if (raro.Length > 0)
                    info.Detalle = (string.IsNullOrEmpty(info.Detalle) ? "" : info.Detalle + " · ") + raro;
            }
            return info;
        }

        private static bool EsSospechoso(string exe, FirmaInfo info)
        {
            if (info.Estado == "dudosa") return true;
            string nombre = Path.GetFileName(exe);
            bool rar = RutaRara(exe).Length > 0;
            if (rar && NombresPrestigiosos.Contains(nombre)) return true;
            // Sin firma y en una carpeta de paso: el patrón típico del descargable suplantado.
            if (info.Estado == "sin_firmar" && rar) return true;
            return false;
        }

        private static string RutaRara(string exe)
        {
            try
            {
                string temp = Path.GetTempPath().TrimEnd('\\');
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (exe.StartsWith(temp + "\\", StringComparison.OrdinalIgnoreCase)) return "vive en una carpeta temporal";
                if (exe.StartsWith(Path.Combine(local, "Temp") + "\\", StringComparison.OrdinalIgnoreCase))
                    return "vive en una carpeta temporal";
                string nombre = Path.GetFileName(exe);
                if (NombresPrestigiosos.Contains(nombre) &&
                    !exe.Contains("\\Program Files", StringComparison.OrdinalIgnoreCase) &&
                    !exe.Contains("\\Windows\\", StringComparison.OrdinalIgnoreCase) &&
                    !exe.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
                    return "no está en la carpeta de instalación";
            }
            catch { }
            return "";
        }

        // ---------- Salida para la interfaz ----------

        public List<FirmaInfo> Resultados()
        {
            lock (_lock)
            {
                return _listo.Values
                    .OrderByDescending(f => f.Sospechosa)
                    .ThenBy(f => f.App, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public FirmaInfo? De(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            lock (_lock) { _listo.TryGetValue(exe, out var f); return f; }
        }

        public int Pendientes
        {
            get { lock (_lock) return _porVer.Count; }
        }

        public int TotalAnalizadas
        {
            get { lock (_lock) return _listo.Count; }
        }

        public static string EstadoLegible(string estado) => estado switch
        {
            "valida" => "Firmada",
            "sin_firmar" => "Sin firmar",
            "dudosa" => "Firma dudosa",
            "ausente" => "Ya no existe",
            _ => "Sin comprobar",
        };

        // ---------- Persistencia ----------

        private void Cargar()
        {
            try
            {
                if (!File.Exists(_fichero)) return;
                var leido = JsonSerializer.Deserialize<Dictionary<string, FilaFirma>>(File.ReadAllText(_fichero));
                if (leido == null) return;
                lock (_lock)
                {
                    _cache.Clear();
                    foreach (var kv in leido) _cache[kv.Key] = kv.Value;
                }
            }
            catch { }
        }

        private void Guardar()
        {
            try
            {
                Dictionary<string, FilaFirma> copia;
                lock (_lock) copia = new Dictionary<string, FilaFirma>(_cache);
                Paths.Ensure();
                File.WriteAllText(_fichero, JsonSerializer.Serialize(copia));
            }
            catch { }
        }

        // Utilidad para el modo de prueba por línea de comandos: comprueba ficheros y escribe
        // el veredicto, sin levantar la interfaz.
        public static string Probar(string[] archivos)
        {
            var sb = new StringBuilder();
            var tabla = Verificar(archivos.ToList());
            foreach (var a in archivos)
            {
                tabla.TryGetValue(a, out var r);
                sb.AppendLine(a + " => " + (string.IsNullOrEmpty(r.Item1) ? "sin respuesta" : r.Item1) +
                              " | " + r.Item2 + " | " + r.Item3);
            }
            return sb.ToString();
        }
    }
}
