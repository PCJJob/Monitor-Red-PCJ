using System;
using System.IO;
using System.Text.Json;
using MonitorRedPCJ.Models;

namespace MonitorRedPCJ.Services
{
    public static class Paths
    {
        public static readonly string Root =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorRedPCJ");
        public static readonly string SettingsFile = Path.Combine(Root, "settings.json");
        public static readonly string AppsFile = Path.Combine(Root, "apps.json");
        public static readonly string EventsFile = Path.Combine(Root, "events.json");
        public static readonly string HistoryFile = Path.Combine(Root, "history.json");
        public static readonly string DevicesFile = Path.Combine(Root, "devices.json");

        public static void Ensure() => Directory.CreateDirectory(Root);
    }

    public class JsonStore<T> where T : new()
    {
        private readonly string _file;
        private static readonly JsonSerializerOptions Opt = new JsonSerializerOptions { WriteIndented = true };

        public JsonStore(string file) { _file = file; }

        public T Load(Func<T> fallback)
        {
            try
            {
                if (File.Exists(_file))
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(_file)) ?? fallback();
            }
            catch { }
            return fallback();
        }

        public void Save(T data)
        {
            try
            {
                Paths.Ensure();
                File.WriteAllText(_file, JsonSerializer.Serialize(data, Opt));
            }
            catch { }
        }
    }

    public class SettingsService
    {
        private readonly JsonStore<AppSettings> _store = new JsonStore<AppSettings>(Paths.SettingsFile);
        public AppSettings Current { get; private set; }

        public SettingsService()
        {
            Current = _store.Load(() => new AppSettings());
        }

        public void Save() => _store.Save(Current);
    }
}
