using System.Windows;

namespace SolidRefrenceRename.WPFUI
{
    /// <summary>
    /// Startup window: launcher for the individual tools.
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void Button_OpenReferenceFixTool_Click(object sender, RoutedEventArgs e)
        {
            new ReferenceFixWindow().Show();
        }

        private void Button_OpenBatchTool_Click(object sender, RoutedEventArgs e)
        {
            new BatchProductWindow().Show();
        }
    }
}
