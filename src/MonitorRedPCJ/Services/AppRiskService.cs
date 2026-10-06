using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MonitorRedPCJ.Services
{
    public enum RiskLevel
    {
        EsencialRed,     // bloquearlo puede dejar el PC sin internet
        SistemaWindows,  // componente de Windows: posibles efectos colaterales
        Aplicacion,      // app de terceros: solo ella pierde la red
        Desconocido      // no se puede clasificar con seguridad
    }

    public class RiskInfo
    {
        public RiskLevel Level { get; set; }
        public string Label { get; set; } = "";       // nombre corto del nivel
        public string LabelOverride { get; set; } = ""; // etiqueta más precisa que la del nivel
        public string What { get; set; } = "";        // qué es esta app
        public string Effect { get; set; } = "";      // qué pasa si la bloqueas
        public string Advice { get; set; } = "";      // recomendación
        public string Publisher { get; set; } = "";

        public bool RiskyToBlock => Level == RiskLevel.EsencialRed || Level == RiskLevel.SistemaWindows;
    }

    // Clasifica una aplicación para decirle al usuario si es seguro cortarle la salida a
    // internet y qué se rompería. Es una ayuda basada en la ruta, el nombre del ejecutable,
    // el editor declarado en el archivo y una tabla de componentes conocidos de Windows.
    public static class AppRisk
    {
        private class Known
        {
            public RiskLevel Level;
            public string What = "";
            public string Effect = "";
        }

        // Componentes de Windows que merecen mención propia. Clave = nombre del exe sin
        // extensión (da igual mayúsculas).
        private static readonly Dictionary<string, Known> Table = new(StringComparer.OrdinalIgnoreCase)
        {
            ["svchost"] = new Known
            {
                Level = RiskLevel.EsencialRed,
                What = "Contenedor de servicios de Windows. Dentro de svchost corren el cliente DNS (Dnscache), " +
                       "el cliente DHCP, Network Location Awareness, el propio Firewall de Windows y las actualizaciones.",
                Effect = "Si le cortas la salida, Windows deja de resolver nombres y de reconocer la red: te quedas " +
                         "sin internet en TODAS las apps, no solo en una."
            },
            ["services"] = new Known
            {
                Level = RiskLevel.EsencialRed,
                What = "Administrador de servicios de Windows.",
                Effect = "Varios servicios de red arrancan desde aquí; bloquearlo puede dejarte sin conexión."
            },
            ["lsass"] = new Known
            {
                Level = RiskLevel.EsencialRed,
                What = "Autoridad de seguridad local: sesión, credenciales y tokens.",
                Effect = "Puede romper tu sesión de Windows, el acceso a redes o la autenticación de Microsoft."
            },
            ["wininit"] = new Known { Level = RiskLevel.EsencialRed, What = "Proceso base del arranque de Windows.", Effect = "Bloquearlo no aporta nada y puede afectar al sistema." },
            ["winlogon"] = new Known { Level = RiskLevel.EsencialRed, What = "Gestor de inicio de sesión.", Effect = "Puede afectar al inicio de sesión y a las credenciales." },
            ["dnsapi"] = new Known { Level = RiskLevel.EsencialRed, What = "Resolución de nombres de Windows.", Effect = "Sin resolución de nombres ninguna app podrá navegar." },
            ["winnat"] = new Known { Level = RiskLevel.SistemaWindows, What = "Traducción de direcciones y compartir conexión (ICS).", Effect = "Si compartes internet desde este PC o usas un emulador/Hyper-V, dejará de funcionar." },
            ["fontdrvhost"] = new Known { Level = RiskLevel.SistemaWindows, What = "Host de controladores de fuente.", Effect = "No necesita internet: bloquearlo es inocuo pero innecesario." },
            ["taskhostw"] = new Known { Level = RiskLevel.SistemaWindows, What = "Ejecuta tareas programadas de Windows.", Effect = "Dejarían de ejecutarse algunas tareas del sistema (mantenimiento, sincronización)." },
            ["runtimebroker"] = new Known { Level = RiskLevel.SistemaWindows, What = "Controla los permisos de las apps de la Tienda.", Effect = "Algunas apps de la Tienda pueden dejar de funcionar bien." },
            ["backgroundtaskhost"] = new Known
            {
                Level = RiskLevel.SistemaWindows,
                What = "Ejecuta el trabajo en segundo plano de las apps de la Tienda (correo, calendario, notificaciones).",
                Effect = "Esas apps dejarán de sincronizarse o de avisar en segundo plano. Windows y tu navegación siguen bien."
            },
            ["searchprotocolhost"] = new Known { Level = RiskLevel.SistemaWindows, What = "Indexador de búsqueda de Windows.", Effect = "La búsqueda local sigue funcionando; solo pierde los resultados web." },
            ["searchhost"] = new Known { Level = RiskLevel.SistemaWindows, What = "Cuadro de búsqueda de la barra de tareas.", Effect = "El menú Inicio deja de mostrar sugerencias web. Todo lo demás sigue." },
            ["shellexperiencehost"] = new Known { Level = RiskLevel.SistemaWindows, What = "Interfaz de notificaciones y centro rápido de Windows.", Effect = "Pueden dejar de salir avisos o accesos de la barra de tareas." },
            ["startmenuexperiencehost"] = new Known { Level = RiskLevel.SistemaWindows, What = "Menú Inicio de Windows 11.", Effect = "Solo pierde el contenido en línea del menú." },
            ["widgets"] = new Known { Level = RiskLevel.SistemaWindows, What = "Widgets del escritorio de Windows 11.", Effect = "Los widgets se quedan sin contenido. Se puede bloquear sin más daño." },
            ["widgetservice"] = new Known { Level = RiskLevel.SistemaWindows, What = "Servicio de los widgets de Windows 11.", Effect = "Igual que widgets: se quedan sin contenido, nada más." },
            ["securityhealthsystray"] = new Known { Level = RiskLevel.SistemaWindows, What = "Icono de Seguridad de Windows en la bandeja.", Effect = "El antivirus sigue activo; solo deja de mostrar avisos en línea." },
            ["msascuil"] = new Known { Level = RiskLevel.SistemaWindows, What = "Interfaz de Seguridad de Windows.", Effect = "No afecta a la protección real del equipo." },
            ["mousocoreworker"] = new Known { Level = RiskLevel.SistemaWindows, What = "Trabajo interno de Windows Update.", Effect = "Windows puede dejar de actualizarse o hacerlo más tarde." },
            ["montagex"] = new Known { Level = RiskLevel.SistemaWindows, What = "Aviso de reinicio de Windows Update.", Effect = "Dejas de ver el aviso; las actualizaciones las gestiona otro proceso." },
            ["usosvc"] = new Known { Level = RiskLevel.SistemaWindows, What = "Orquestador de actualizaciones de Windows.", Effect = "Windows dejará de actualizarse (parches de seguridad incluidos)." },
            ["explorer"] = new Known { Level = RiskLevel.SistemaWindows, What = "Escritorio y barra de tareas de Windows.", Effect = "Pierde contenido en línea; si notas algo raro en el escritorio, quítale el bloqueo." },
            ["dllhost"] = new Known { Level = RiskLevel.SistemaWindows, What = "Host de complementos COM (miniaturas, vista previa).", Effect = "Algunas vistas previas y miniaturas dejarán de cargarse contenido externo." },
            ["spoolsv"] = new Known { Level = RiskLevel.SistemaWindows, What = "Cola de impresión de Windows.", Effect = "Las impresoras de red y el imprimir en la nube dejarán de funcionar." },
            ["dashost"] = new Known { Level = RiskLevel.SistemaWindows, What = "Asociación de dispositivos.", Effect = "Algunos dispositivos Bluetooth/USB pueden tardar más en emparejarse." },
            ["wudfhost"] = new Known { Level = RiskLevel.SistemaWindows, What = "Host de controladores de usuario (WUDF).", Effect = "Ciertos dispositivos podrían dejar de funcionar." },
            ["ccxprocess"] = new Known { Level = RiskLevel.SistemaWindows, What = "Proceso de conectividad de aplicaciones de la Tienda.", Effect = "Algunas apps de la Tienda perderán funciones en línea." },
            ["installservice"] = new Known { Level = RiskLevel.SistemaWindows, What = "Instalador de Windows (msiserver).", Effect = "No podrás instalar programas que descarguen componentes." },
        };

        private static readonly string[] Browsers =
            { "chrome", "msedge", "firefox", "opera", "opera_gui", "brave", "vivaldi", "epic", "ucbrowser", "torbrowser" };

        private static readonly string[] UpdaterHints =
            { "update", "updater", "crashpad", "telemetry", "installer", "setup", "uninstall", "launcher" };

        private static readonly string[] SecurityVendors =
            { "adguard", "kaspersky", "eset", "nod32", "avast", "avg", "mcafee", "norton", "bitdefender",
              "malwarebytes", "trend micro", "comodo", "f-secure", "gdata", "panda", "sentinel", "crowdstrike", "defender" };

        private static readonly string[] DevTools =
            { "qoder", "code", "devenv", "idea64", "studio64", "java", "python", "node", "codex", "chatgpt", "adb", "dotnet", "git" };

        public static RiskInfo Analyze(string exePath)
        {
            var info = new RiskInfo();

            if (string.IsNullOrWhiteSpace(exePath))
            {
                info.Level = RiskLevel.Desconocido;
                info.Label = "Sin datos";
                info.What = "El proceso ya no informa de su ruta (terminó o está protegido).";
                info.Effect = "No se puede evaluar: decide solo si reconoces la aplicación por su nombre.";
                info.Advice = "Vuelve a preguntar cuando la veas en la lista.";
                return info;
            }

            string fileName = Path.GetFileNameWithoutExtension(exePath) ?? "";
            string lower = fileName.ToLowerInvariant();
            string lowerPath = exePath.ToLowerInvariant();

            string publisher = "";
            string original = "";
            try
            {
                if (File.Exists(exePath))
                {
                    var vi = FileVersionInfo.GetVersionInfo(exePath);
                    publisher = (vi.CompanyName ?? "").Trim();
                    original = Path.GetFileNameWithoutExtension((vi.OriginalFilename ?? "").Trim()).ToLowerInvariant();
                }
            }
            catch { }
            info.Publisher = publisher;

            bool enWindows = lowerPath.StartsWith(@"c:\windows", StringComparison.OrdinalIgnoreCase);
            bool firmaMicrosoft = publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

            if (Table.TryGetValue(lower, out var k) ||
                (!string.IsNullOrEmpty(original) && Table.TryGetValue(original, out k)))
            {
                info.Level = k.Level;
                info.What = k.What;
                info.Effect = k.Effect;
            }
            else if (Browsers.Contains(lower) || Browsers.Contains(original))
            {
                info.Level = RiskLevel.Aplicacion;
                info.What = "Es un navegador web" + (string.IsNullOrEmpty(publisher) ? "." : " (editor: " + publisher + ").");
                info.Effect = "Si lo bloqueas no podrás abrir ninguna página con él: es el corte que antes vas a notar.";
            }
            else if (SecurityVendors.Any(v => lowerPath.Contains(v) || publisher.ToLowerInvariant().Contains(v)))
            {
                info.Level = RiskLevel.SistemaWindows;
                info.LabelOverride = "Programa de seguridad";
                info.What = "Pertenece a un programa de seguridad, control parental o bloqueo de anuncios" +
                            (string.IsNullOrEmpty(publisher) ? "." : " (" + publisher + ").");
                info.Effect = "Sin salida no podrá actualizar sus listas ni sus avisos: la protección se queda desfasada, " +
                              "aunque lo que ya tengas bloqueada sigue funcionando.";
            }
            else if (enWindows)
            {
                info.Level = RiskLevel.SistemaWindows;
                info.What = "Componente de Windows" + (firmaMicrosoft ? " firmado por Microsoft" : "") + ": " + fileName + ".";
                info.Effect = "No está catalogado como esencial para la red, pero muchos procesos de C:\\Windows dan servicio " +
                              "a otros. Si tras bloquearlo notas algo raro, quítale el bloqueo antes de seguir.";
            }
            else if (UpdaterHints.Any(h => lower.Contains(h)))
            {
                info.Level = RiskLevel.Aplicacion;
                info.What = "Actualizador o componente en segundo plano de otra aplicación" +
                            (string.IsNullOrEmpty(publisher) ? "." : " (" + publisher + ").");
                info.Effect = "Solo deja de actualizarse esa aplicación. Windows y el resto de tus programas siguen igual.";
            }
            else if (DevTools.Contains(lower) || DevTools.Contains(original))
            {
                info.Level = RiskLevel.Aplicacion;
                info.What = "Herramienta de desarrollo o asistente" + (string.IsNullOrEmpty(publisher) ? "." : " de " + publisher + ".");
                info.Effect = "Dejará de funcionar todo lo que dependa de la nube: modelos, extensiones, repositorios, sincronización.";
            }
            else if (!string.IsNullOrEmpty(publisher) || lowerPath.Contains("\\program files") || lowerPath.Contains("\\appdata"))
            {
                info.Level = RiskLevel.Aplicacion;
                info.What = "Aplicación de terceros" + (string.IsNullOrEmpty(publisher) ? "." : " (editor: " + publisher + ").");
                info.Effect = "Solo esa aplicación pierde la conexión: dejará de sincronizar, actualizar o cargar contenido. " +
                              "Windows seguirá teniendo internet normal.";
            }
            else
            {
                info.Level = RiskLevel.Desconocido;
                info.What = "No se ha podido clasificar: " + fileName + ".";
                info.Effect = "Bloquearlo solo afectaría a esa app, pero como no hay datos del archivo, revisa la ruta antes de decidir.";
            }

            info.Label = info.Level switch
            {
                RiskLevel.EsencialRed => "Esencial para tener internet",
                RiskLevel.SistemaWindows => "Componente de Windows",
                RiskLevel.Aplicacion => "Aplicación normal",
                _ => "Sin clasificar"
            };
            if (!string.IsNullOrEmpty(info.LabelOverride)) info.Label = info.LabelOverride;
            info.Advice = info.Level switch
            {
                RiskLevel.EsencialRed => "No lo bloquees salvo que sepas exactamente qué haces: puedes quedarte sin red en todo el equipo.",
                RiskLevel.SistemaWindows => "Se puede bloquear, pero espera efectos en funciones de Windows. Si notas algo raro, quita el bloqueo.",
                RiskLevel.Aplicacion => "Seguro de bloquear: solo esa aplicación se queda sin salida.",
                _ => "No hay datos suficientes para recomendarte una opción: decide tú."
            };
            return info;
        }
    }
}
