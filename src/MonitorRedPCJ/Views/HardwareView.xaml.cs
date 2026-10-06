using System;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MonitorRedPCJ.Views
{
    public partial class HardwareView : UserControl
    {
        private readonly DispatcherTimer _timer;

        public HardwareView()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Sample();
            // Muestrear CPU, disco, GPU y red cuesta y no sirve de nada con la pestaña cerrada:
            // el temporizador solo corre mientras se ve.
            _timer.Start();
            if (!IsVisible) _timer.Stop();
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible)
                {
                    // La primera muestra lee contadores de rendimiento y eso tarda; se hace un
                    // tick después del clic para que cambiar de pestaña no se note pesado.
                    Dispatcher.BeginInvoke(new Action(Sample),
                        System.Windows.Threading.DispatcherPriority.Loaded);
                    _timer.Start();
                }
                else _timer.Stop();
            };
            Unloaded += (s, e) => _timer.Stop();
        }

        private void Sample()
        {
            App.Hardware.Sample();
            var hw = App.Hardware;
            var (rx, tx, rxBps, txBps) = App.Traffic.GetSessionTotals();
            double disk = hw.DiskReadBps + hw.DiskWriteBps;
            double net = rxBps + txBps;

            ChartCpu.Push(hw.CpuPercent);
            ChartMem.Push(hw.MemoryUsedGb);
            ChartDisk.Push(disk);
            ChartNet.Push(net);

            string cpu = hw.CpuPercent.ToString("0") + " %";
            string mem = hw.MemoryUsedGb.ToString("0.0") + " GB";
            string diskTxt = Format.Bps(disk);
            string netTxt = Format.Bps(net);

            ValCpu.Text = cpu;
            ValMem.Text = mem;
            ValDisk.Text = diskTxt;
            ValNet.Text = netTxt;

            PeakCpu.Text = "máx " + ChartCpu.WindowPeak.ToString("0") + " %";
            PeakMem.Text = "de " + hw.MemoryTotalGb.ToString("0.0") + " GB";
            PeakDisk.Text = "máx " + Format.Bps(ChartDisk.WindowPeak);
            PeakNet.Text = "máx " + Format.Bps(ChartNet.WindowPeak);

            TileCpu.Text = cpu;
            TileMem.Text = mem;
            TileDisk.Text = Format.Bps(hw.DiskReadBps);
            TileNet.Text = netTxt;
            TileGpu.Text = hw.GpuPercent < 0 ? "N/D" : hw.GpuPercent.ToString("0") + " %";

            if (hw.DiskTempC.HasValue)
            {
                TileTemp.Text = ((int)System.Math.Round(hw.DiskTempC.Value)).ToString() + " °C";
                TileTemp.ToolTip = string.IsNullOrEmpty(hw.DiskTempModel)
                    ? "Temperatura del disco" : "Temperatura de " + hw.DiskTempModel;
                // Verde en frío, ámbar al calentarse, coral si pasa de 60 °C.
                TileTemp.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current
                    .FindResource(hw.DiskTempC >= 60 ? "B.Coral" : hw.DiskTempC >= 50 ? "B.Amber" : "B.Green");
            }
            else
            {
                TileTemp.Text = "—";
                TileTemp.ToolTip = "Leyendo la temperatura del disco… (algunos equipos la ocultan " +
                    "si el monitor no tiene permisos de administrador)";
            }
        }
    }
}
