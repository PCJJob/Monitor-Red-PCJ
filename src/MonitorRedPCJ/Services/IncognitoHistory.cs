using System;
using System.Collections.Generic;
using MonitorRedPCJ.Models;

namespace MonitorRedPCJ.Services
{
    // Lo que se recupera al salir del incógnito: la foto del histórico que había antes de
    // entrar, pero con las decisiones que se tomaran durante ese rato por encima.
    //
    // Se separa del servicio por dos motivos: es la parte delicada (si se equivoca deshace
    // permisos que el usuario dio a mano) y así se puede probar sin levantar el monitor.
    public static class IncognitoHistory
    {
        public static Dictionary<string, TrackedApp> Merge(
            Dictionary<string, TrackedApp> foto,
            Dictionary<string, TrackedApp> actuales)
        {
            var dict = new Dictionary<string, TrackedApp>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in foto)
            {
                var app = kv.Value;
                if (app == null) continue;
                if (string.IsNullOrEmpty(app.ExePath)) app.ExePath = kv.Key;

                // Decidir quién sale o quién no no es histórico: si durante el incógnito se
                // permitió o se cortó esta app, la decisión gana a la foto. Lo que se recupera
                // son las cuentas de bytes, los destinos y el "primera vez que se vio".
                if (actuales.TryGetValue(app.ExePath, out var viva) && viva != null && viva.IsKnown)
                {
                    app.OutAction = viva.OutAction;
                    app.InAction = viva.InAction;
                    app.IsKnown = true;
                    app.AskedWithoutDeciding = viva.AskedWithoutDeciding;
                    app.AskOnNextLaunch = viva.AskOnNextLaunch;
                }
                dict[app.ExePath] = app;
            }

            // Programa visto por primera vez en el tramo en incógnito: solo se conserva si
            // recayó sobre él una decisión; si no, ese rato no deja rastro.
            foreach (var kv in actuales)
            {
                if (dict.ContainsKey(kv.Key)) continue;
                var viva = kv.Value;
                if (viva == null || !viva.IsKnown) continue;
                dict[kv.Key] = new TrackedApp
                {
                    ExePath = string.IsNullOrEmpty(viva.ExePath) ? kv.Key : viva.ExePath,
                    Name = viva.Name,
                    FirstSeenUtc = viva.FirstSeenUtc,
                    OutAction = viva.OutAction,
                    InAction = viva.InAction,
                    IsKnown = true,
                    AskedWithoutDeciding = viva.AskedWithoutDeciding,
                    AskOnNextLaunch = viva.AskOnNextLaunch,
                };
            }

            return dict;
        }
    }
}
