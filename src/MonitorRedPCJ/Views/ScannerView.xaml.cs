using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MonitorRedPCJ.Services;

namespace MonitorRedPCJ.Views
{
    public partial class ScannerView : UserControl
    {
        public class Row
        {
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string Ip { get; set; } = "";
            public string Mac { get; set; } = "";
            public string Seen { get; set; } = "";
        }

        public ScannerView()
        {
            InitializeComponent();
            ChkAuto.IsChecked = App.Settings.Current.AutoScanNetwork;
            App.Scanner.ScanCompleted += () => Dispatcher.BeginInvoke(Refresh);
            Refresh();
        }

        private void OnScan(object sender, RoutedEventArgs e)
        {
            BtnScan.IsEnabled = false;
            TxtScanned.Text = "Escaneando...";
            App.Scanner.ScanAsync();
            var t = new System.Threading.Timer(_ => Dispatcher.BeginInvoke(() => BtnScan.IsEnabled = true),
                null, 5000, Timeout.Infinite);
        }

        private void OnAuto(object sender, RoutedEventArgs e)
        {
            App.Settings.Current.AutoScanNetwork = ChkAuto.IsChecked == true;
            App.Settings.Save();
        }

        private void Refresh()
        {
            TxtNetwork.Text = App.Scanner.CurrentNetworkName();
            DevicesList.ItemsSource = App.Scanner.Devices.Select(d => new Row
            {
                Name = string.IsNullOrEmpty(d.Name) ? d.Ip : d.Name,
                Description = d.Description,
                Ip = d.Ip,
                Mac = d.Mac,
                Seen = d.LastSeenUtc.ToLocalTime().ToString("dd MMM, HH:mm"),
            }).ToList();
        }
    }
}
