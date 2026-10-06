using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorRedPCJ.Models;
using MonitorRedPCJ.Services;

namespace MonitorRedPCJ.Views
{
    public partial class LogView : UserControl
    {
        public class Row
        {
            public string Message { get; set; } = "";
            public string Detail { get; set; } = "";
            public Visibility DetailVis { get; set; } = Visibility.Visible;
            public string KindText { get; set; } = "";
            public string TimeText { get; set; } = "";
            public Brush Badge { get; set; } = Brushes.Gray;
            public string BadgeText { get; set; } = "";
        }

        public LogView()
        {
            InitializeComponent();
            App.Events.EventAdded += OnChanged;
            Unloaded += (s, e) => App.Events.EventAdded -= OnChanged;
            Refresh();
        }

        private void OnChanged() => Dispatcher.BeginInvoke(Refresh);

        private void OnFilter(object sender, SelectionChangedEventArgs e) => Refresh();

        private void OnMarkRead(object sender, RoutedEventArgs e) => App.Events.MarkAllRead();

        private void Refresh()
        {
            if (EventsList == null) return;
            bool onlyImportant = FilterCombo.SelectedIndex == 0;
            int days = TimeCombo.SelectedIndex == 1 ? 0 : TimeCombo.SelectedIndex == 2 ? 7 : 99999;

            var rows = App.Events.Snapshot()
                .Where(ev => !onlyImportant || ev.Important)
                .Where(ev => days == 99999 ||
                             (DateTime.UtcNow - ev.TimeUtc).TotalDays <= days + 1)
                .Take(500)
                .Select(ev => new Row
                {
                    Message = ev.Message,
                    Detail = ev.Detail,
                    DetailVis = string.IsNullOrEmpty(ev.Detail) ? Visibility.Collapsed : Visibility.Visible,
                    KindText = KindLabel(ev.Kind),
                    TimeText = ev.TimeUtc.ToLocalTime().ToString("HH:mm"),
                    Badge = BadgeBrush(ev),
                    BadgeText = ev.Read ? "" : "NUEVO",
                }).ToList();
            EventsList.ItemsSource = rows;
        }

        private static string KindLabel(EventKind k) => k switch
        {
            EventKind.FirstConnection => "Primera actividad",
            EventKind.RuleChanged => "Firewall",
            EventKind.NewDevice => "Escáner de red",
            EventKind.DeviceGone => "Escáner de red",
            EventKind.ProtectionToggled => "Protección",
            _ => "Info",
        };

        private static Brush BadgeBrush(NetEvent ev)
        {
            if (!ev.Read) return (Brush)Application.Current.FindResource("B.Green");
            return ev.Kind switch
            {
                EventKind.RuleChanged => (Brush)Application.Current.FindResource("B.Coral"),
                EventKind.NewDevice => (Brush)Application.Current.FindResource("B.Indigo"),
                _ => (Brush)Application.Current.FindResource("B.Line"),
            };
        }
    }
}
