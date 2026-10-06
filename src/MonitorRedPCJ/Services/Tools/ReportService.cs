using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MonitorRedPCJ.Models;

namespace MonitorRedPCJ.Services.Tools
{
    public class SeccionInforme
    {
        public string Id { get; set; } = "";
        public string Nombre { get; set; } = "";
        public bool Marcada { get; set; } = true;
    }

    /// Informe de seguridad: monta HTML o CSV con lo que PCJ ya tiene en disco. No manda nada
    /// a ningún sitio y no consulta redes: es lectura y escritura local.
    public class ReportService
    {
        public static ReportService? Instancia { get; private set; }

        private readonly string _carpeta;

        public ReportService()
        {
            Instancia = this;
            _carpeta = Path.Combine(Paths.Root, "informes");
        }

        public void Start() { }
        public void Stop() { }

        public string Carpeta
        {
            get
            {
                try { Paths.Ensure(); Directory.CreateDirectory(_carpeta); } catch { }
                return _carpeta;
            }
        }

        public static readonly (string id, string nombre)[] Secciones =
        {
            ("resumen", "Resumen del periodo"),
            ("apps", "Aplicaciones que más han salido"),
            ("rastreo", "Destinos de rastreo vistos"),
            ("bloqueos", "Intentos de salida bloqueados"),
            ("paises", "País de cada destino"),
            ("puertos", "Puertos expuestos"),
            ("dispositivos", "Equipos de la red"),
            ("herramientas", "Estado de las herramientas"),
        };

        public string UltimoInforme
        {
            get
            {
                try
                {
                    if (!Directory.Exists(_carpeta)) return "";
                    return new DirectoryInfo(_carpeta).GetFiles("pcj-informe-*")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .Select(f => f.FullName)
                        .FirstOrDefault() ?? "";
                }
                catch { return ""; }
            }
        }

        // ---------- Periodo ----------

        public static DateTime InicioDe(string periodo)
        {
            var hoy = DateTime.Today;
            switch (periodo)
            {
                case "hoy": return hoy;
                case "7": return hoy.AddDays(-7);
                case "30": return hoy.AddDays(-30);
                default: return DateTime.MinValue;
            }
        }

        public static string Etiqueta(string periodo) => periodo switch
        {
            "hoy" => "hoy",
            "7" => "los últimos 7 días",
            "30" => "los últimos 30 días",
            _ => "todo el tiempo que lleva PCJ instalado",
        };

        // ---------- Generación ----------

        public string GenerarHTML(string periodo, IEnumerable<string> marcadas, out string error)
        {
            error = "";
            try
            {
                var datos = Recolectar(periodo, marcadas);
                var sb = new StringBuilder();
                sb.AppendLine("<!DOCTYPE html>");
                sb.AppendLine("<html lang=\"es\"><head><meta charset=\"utf-8\">");
                sb.AppendLine("<title>Informe PCJ — " + Escape(datos.Titulo) + "</title>");
                sb.AppendLine("<style>");
                sb.AppendLine(":root{--bg:#0E1B22;--panel:#152731;--line:#26414F;--text:#EAF3F8;--dim:#9FB6C2;" +
                              "--ok:#31C48D;--warn:#E8912A;--bad:#E4574F;--brand:#0FA3A3}");
                sb.AppendLine("body{margin:0;padding:34px;background:var(--bg);color:var(--text);" +
                              "font-family:'Segoe UI Variable Text','Segoe UI',sans-serif;font-size:14.5px;line-height:1.5}");
                sb.AppendLine("h1{font-size:26px;margin:0 0 4px}h2{font-size:17px;margin:30px 0 10px;color:var(--brand)}");
                sb.AppendLine(".sub{color:var(--dim);margin-bottom:26px}");
                sb.AppendLine(".card{background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:16px 18px;margin:0 0 14px}");
                sb.AppendLine(".grid{display:flex;flex-wrap:wrap;gap:12px}");
                sb.AppendLine(".kpi{background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:12px 16px;min-width:150px}");
                sb.AppendLine(".kpi b{display:block;font-size:22px}");
                sb.AppendLine(".kpi span{color:var(--dim);font-size:12px}");
                sb.AppendLine("table{border-collapse:collapse;width:100%;font-size:13.5px}");
                sb.AppendLine("th{text-align:left;color:var(--dim);font-weight:600;border-bottom:1px solid var(--line);padding:7px 8px}");
                sb.AppendLine("td{border-bottom:1px solid rgba(38,65,79,.55);padding:6px 8px;vertical-align:top}");
                sb.AppendLine(".ok{color:var(--ok)}.warn{color:var(--warn)}.bad{color:var(--bad)}");
                sb.AppendLine(".foot{color:var(--dim);font-size:12px;margin-top:32px}");
                sb.AppendLine("</style></head><body>");
                sb.AppendLine("<h1>Monitor de Red PCJ — informe</h1>");
                sb.AppendLine("<div class=\"sub\">" + Escape(datos.Titulo) + "</div>");

                sb.AppendLine("<div class=\"grid\">");
                foreach (var k in datos.Kpis)
                    sb.AppendLine("<div class=\"kpi\"><b>" + Escape(k.Item1) + "</b><span>" + Escape(k.Item2) + "</span></div>");
                sb.AppendLine("</div>");

                foreach (var bloque in datos.Bloques)
                {
                    sb.AppendLine("<h2>" + Escape(bloque.Nombre) + "</h2>");
                    if (bloque.Filasal.Count == 0)
                    {
                        sb.AppendLine("<div class=\"card\">Nada que mostrar en este apartado dentro del periodo.</div>");
                        continue;
                    }
                    sb.AppendLine("<div class=\"card\"><table><thead><tr>");
                    foreach (var cab in bloque.Cabeceras) sb.AppendLine("<th>" + Escape(cab) + "</th>");
                    sb.AppendLine("</tr></thead><tbody>");
                    foreach (var fila in bloque.Filasal)
                    {
                        sb.AppendLine("<tr>");
                        foreach (var celda in fila) sb.AppendLine("<td>" + Escape(celda) + "</td>");
                        sb.AppendLine("</tr>");
                    }
                    sb.AppendLine("</tbody></table></div>");
                }

                sb.AppendLine("<div class=\"foot\">Hecho por Monitor de Red PCJ con los datos guardados en tu " +
                              "equipo (%AppData%\\MonitorRedPCJ). No se envió nada a ningún servidor.</div>");
                sb.AppendLine("</body></html>");

                string ruta = RutaNuevo("html");
                File.WriteAllText(ruta, sb.ToString(), new UTF8Encoding(false));
                return ruta;
            }
            catch (Exception ex) { error = ex.Message; return ""; }
        }

        public string GenerarCSV(string periodo, IEnumerable<string> marcadas, out string error)
        {
            error = "";
            try
            {
                var datos = Recolectar(periodo, marcadas);
                var sb = new StringBuilder();
                sb.AppendLine("seccion;campo;valor");
                foreach (var k in datos.Kpis)
                    sb.AppendLine(Csv("resumen") + ";" + Csv(k.Item2) + ";" + Csv(k.Item1));
                foreach (var bloque in datos.Bloques)
                {
                    for (int i = 0; i < bloque.Cabeceras.Count; i++)
                        sb.AppendLine(Csv(bloque.Id) + ";cabecera" + i + ";" + Csv(bloque.Cabeceras[i]));
                    foreach (var fila in bloque.Filasal)
                        for (int i = 0; i < fila.Count; i++)
                            sb.AppendLine(Csv(bloque.Id) + ";fila" + i + ";" + Csv(string.Join(" | ", fila)));
                }
                string ruta = RutaNuevo("csv");
                File.WriteAllText(ruta, sb.ToString(), new UTF8Encoding(true));   // con BOM: Excel lo pide
                return ruta;
            }
            catch (Exception ex) { error = ex.Message; return ""; }
        }

        private string RutaNuevo(string ext)
        {
            string carpeta = Carpeta;
            string nombre = "pcj-informe-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture) +
                            "." + ext;
            return Path.Combine(carpeta, nombre);
        }

        private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        // ---------- Los datos ----------

        private class Bloque
        {
            public string Id { get; set; } = "";
            public string Nombre { get; set; } = "";
            public List<string> Cabeceras { get; set; } = new();
            public List<List<string>> Filasal = new();
        }

        private class DatosInforme
        {
            public string Titulo { get; set; } = "";
            public List<(string, string)> Kpis = new();
            public List<Bloque> Bloques = new();
        }

        private DatosInforme Recolectar(string periodo, IEnumerable<string> marcadas)
        {
            var set = new HashSet<string>(marcadas ?? Enumerable.Empty<string>());
            var desde = InicioDe(periodo);
            var d = new DatosInforme
            {
                Titulo = "Periodo: " + Etiqueta(periodo) + " · generado el " +
                         DateTime.Now.ToString("dd/MM/yyyy HH:mm"),
            };

            var apps = new List<TrackedApp>();
            try { apps = App.Traffic.GetApps().ToList(); } catch { }
            var eventos = new List<NetEvent>();
            try { eventos = App.Events.Snapshot().Where(e => e.TimeUtc.ToLocalTime() >= desde).ToList(); } catch { }

            long rx = 0, tx = 0;
            foreach (var a in apps) { rx += a.TotalReceived; tx += a.TotalSent; }

            var cuotas = QuotaService.Instancia;
            d.Kpis.Add((Format.Bytes(rx + tx), "movidos por las apps conocidas"));
            d.Kpis.Add((apps.Count.ToString(CultureInfo.InvariantCulture), "aplicaciones en el histórico"));
            d.Kpis.Add((eventos.Count.ToString(CultureInfo.InvariantCulture), "avisos en el registro"));
            int importantes = eventos.Count(e => e.Important);
            d.Kpis.Add((importantes.ToString(CultureInfo.InvariantCulture), "de ellos, importantes"));
            try { d.Kpis.Add((App.Firewall.AllowRuleCount() + " / " + App.Firewall.BlockRuleCount(),
                              "permisos de salida / bloqueos en Windows")); } catch { }
            if (cuotas != null) d.Kpis.Add((Format.Bytes(cuotas.TotalDelMes.bytes), "en el periodo de facturación"));

            if (set.Contains("apps"))
            {
                var b = new Bloque { Id = "apps", Nombre = "Aplicaciones que más han salido" };
                b.Cabeceras.AddRange(new[] { "Aplicación", "Enviado", "Recibido", "Destinos", "Salida" });
                foreach (var a in apps.OrderByDescending(a => a.TotalSent + a.TotalReceived).Take(30))
                {
                    DecisionLegible(a, out string decision);
                    b.Filasal.Add(new List<string>
                    {
                        Path.GetFileName(a.ExePath), Format.Bytes(a.TotalSent), Format.Bytes(a.TotalReceived),
                        a.Hosts.Count.ToString(CultureInfo.InvariantCulture), decision,
                    });
                }
                d.Bloques.Add(b);
            }

            if (set.Contains("rastreo"))
            {
                var b = new Bloque { Id = "rastreo", Nombre = "Destinos de rastreo vistos" };
                b.Cabeceras.AddRange(new[] { "Aplicación", "Destino", "IP", "Bloqueado en hosts" });
                var bloqueados = new HashSet<string>(RadarService.Bloqueados(), StringComparer.OrdinalIgnoreCase);
                var radar = RadarService.Instancia;
                if (radar != null)
                {
                    foreach (var x in radar.DestinosHistoricos().Where(x => x.Rastreador).Take(120))
                        b.Filasal.Add(new List<string>
                        {
                            x.App, x.Host.Length > 0 ? x.Host : "(sin nombre)", x.Ip,
                            bloqueados.Contains(x.Host) ? "sí" : "no",
                        });
                }
                d.Bloques.Add(b);
            }

            if (set.Contains("bloqueos"))
            {
                var b = new Bloque { Id = "bloqueos", Nombre = "Intentos de salida y avisos del monitor" };
                b.Cabeceras.AddRange(new[] { "Cuándo", "Qué pasó", "Detalle" });
                foreach (var e in eventos.OrderByDescending(e => e.TimeUtc).Take(120))
                    b.Filasal.Add(new List<string>
                    {
                        e.TimeUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), e.Message, e.Detail,
                    });
                d.Bloques.Add(b);
            }

            if (set.Contains("paises"))
            {
                var b = new Bloque { Id = "paises", Nombre = "País de cada destino" };
                b.Cabeceras.AddRange(new[] { "País", "Destinos", "Aplicaciones" });
                var geo = GeoService.Instancia;
                if (geo != null)
                    foreach (var g in geo.Paises().Take(40))
                        b.Filasal.Add(new List<string>
                        {
                            g.Nombre + " (" + g.Codigo + ")", g.Destinos.ToString(CultureInfo.InvariantCulture),
                            string.Join(", ", g.Apps.Take(6)),
                        });
                d.Bloques.Add(b);
            }

            if (set.Contains("puertos"))
            {
                var b = new Bloque { Id = "puertos", Nombre = "Puertos expuestos" };
                b.Cabeceras.AddRange(new[] { "Aplicación", "Puerto", "Protocolo", "Desde dónde", "Servicio" });
                var puertos = PortAuditService.Instancia;
                if (puertos != null)
                    foreach (var e in puertos.Escuchadores().Where(e => e.Alcance > 0).Take(60))
                        b.Filasal.Add(new List<string>
                        {
                            e.App, e.Puerto.ToString(CultureInfo.InvariantCulture), e.EsTcp ? "TCP" : "UDP",
                            PortAuditService.AlcanceTexto(e.Alcance), e.Servicio,
                        });
                d.Bloques.Add(b);
            }

            if (set.Contains("dispositivos"))
            {
                var b = new Bloque { Id = "dispositivos", Nombre = "Equipos de la red" };
                b.Cabeceras.AddRange(new[] { "Nombre", "IP", "MAC", "Visto por última vez" });
                foreach (var dev in App.Scanner.Devices.Take(80))
                    b.Filasal.Add(new List<string>
                    {
                        dev.Name, dev.Ip, dev.Mac, dev.LastSeenUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                    });
                d.Bloques.Add(b);
            }

            if (set.Contains("herramientas"))
            {
                var b = new Bloque { Id = "herramientas", Nombre = "Estado de las herramientas" };
                b.Cabeceras.AddRange(new[] { "Herramienta", "Encendida" });
                foreach (var t in ToolCatalog.Todas)
                    b.Filasal.Add(new List<string>
                    {
                        t.Nombre, t.EsExistente ? "siempre" : (ToolCatalog.Activada(t.Id) ? "sí" : "no"),
                    });
                d.Bloques.Add(b);
            }

            return d;
        }

        private static void DecisionLegible(TrackedApp a, out string decision)
        {
            try
            {
                var d = App.Firewall.GetDecision(a.ExePath, Direction.Out);
                decision = d switch
                {
                    Decision.Permitido => "permitida",
                    Decision.Bloqueado => "sin salida",
                    _ => App.Settings.Current.StrictMode ? "sin salida (sin decidir)" : "sin decidir",
                };
            }
            catch { decision = ""; }
        }

        private static string Escape(string s) =>
            (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
