using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MonitorRedPCJ.Models;
using MonitorRedPCJ.Services;

namespace MonitorRedPCJ.Views
{
    public partial class TrafficView : UserControl
    {
        public class AppRow
        {
            public string Name { get; set; } = "";
            public string RxText { get; set; } = "";
            public string TxText { get; set; } = "";
            public Visibility NewTag { get; set; } = Visibility.Collapsed;
        }

        public class HostRow
        {
            public string Host { get; set; } = "";
            public string App { get; set; } = "";
            public string Total { get; set; } = "";
        }

        private readonly Dictionary<string, DateTime> _newShown = new();

        public TrafficView()
        {
            InitializeComponent();
            App.Traffic.DataUpdated += OnUpdate;
            Unloaded += (s, e) => App.Traffic.DataUpdated -= OnUpdate;
            IsVisibleChanged += (s, e) =>
            {
                if (!IsVisible || !_deferred) return;
                _deferred = false;
                // Un tick más tarde: el clic de la pestaña responde al instante y el gráfico
                // se redibuja justo después (hacerlo dentro del clic costaba ~200 ms).
                Dispatcher.BeginInvoke(new Action(Refresh),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            };
        }

        private bool _deferred;

        private void OnUpdate()
        {
            // Con la pestaña cerrada no se pinta nada: reconstruir dos listas por segundo
            // para una ventana oculta era CPU tirada. Al volver a abrirse se pone al día.
            if (!IsVisible) { _deferred = true; return; }
            _deferred = false;
            Dispatcher.BeginInvoke(Refresh);
        }

        private void Refresh()
        {
            var (rx, tx, rxBps, txBps) = App.Traffic.GetSessionTotals();
            Graph.Push(rxBps, txBps);
            LegendRx.Text = Format.Bps(rxBps);
            LegendTx.Text = Format.Bps(txBps);

            var apps = App.Traffic.GetApps();
            var rows = apps
                .Where(a => a.TotalSent + a.TotalReceived > 0 || a.Hosts.Count > 0)
                .Take(15)
                .Select(a => new AppRow
                {
                    Name = a.Name,
                    RxText = Format.Bps(a.ReceivedBps),
                    TxText = Format.Bps(a.SentBps),
                    NewTag = IsRecent(a.FirstSeenUtc) ? Visibility.Visible : Visibility.Collapsed,
                }).ToList();
            AppsList.ItemsSource = rows;

            var hostRows = new List<HostRow>();
            foreach (var a in apps)
                foreach (var h in App.Traffic.HostsOf(a)
                             .Where(h => !TrafficService.IsLocalOrPrivate(h))
                             .OrderBy(h => h)
                             .Take(3))
                    hostRows.Add(new HostRow { Host = h, App = a.Name, Total = Format.Bytes(a.TotalSent + a.TotalReceived) });
            HostsList.ItemsSource = hostRows
                .GroupBy(r => r.Host).Select(g => g.First()).Take(15).ToList();
        }

        private bool IsRecent(DateTime firstSeenUtc)
            => (DateTime.UtcNow - firstSeenUtc).TotalMinutes < 10;
    }
}
