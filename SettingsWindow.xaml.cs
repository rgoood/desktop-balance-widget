using System.Windows;

namespace DesktopWidget
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _main;

        public SettingsWindow(WidgetConfig config, MainWindow main)
        {
            InitializeComponent();
            _main = main;
            TxtKey.Text = config.OpenRouterApiKey;
            TxtSubUrl.Text = config.AirportSubscriptionUrl;
            TxtInterval.Text = config.RefreshIntervalHours.ToString("0.#");
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var cfg = new WidgetConfig
            {
                OpenRouterApiKey = TxtKey.Text.Trim(),
                AirportSubscriptionUrl = TxtSubUrl.Text.Trim(),
                RefreshIntervalHours = double.TryParse(TxtInterval.Text, out var h) && h > 0 ? h : 2.0,
                EmbedDesktop = _main.CurrentConfig.EmbedDesktop,
            };
            _main.ApplyConfig(cfg);
            Close();
        }
    }
}
