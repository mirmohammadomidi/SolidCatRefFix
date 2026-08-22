using SolidRefrenceRename.WPFUI.ViewModel;
using SolidRefrenceRename.WPFUI.Lib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace SolidRefrenceRename.WPFUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private int _totalNumberOfParts;
        private int _totalNumberOfAssemblies;
        private int _totalNumberOfDrawings;
        public SolidFixViewModel ViewModel => (SolidFixViewModel)this.DataContext;
        public MainWindow()
        {
            DataContext = new SolidFixViewModel();
            InitializeComponent();
            ViewModel.OnNumberOfAssembliesChanged += ViewModel_OnNumberOfAssembliesChanged;
            ViewModel.OnNumberOfPartsChanged += ViewModel_OnNumberOfPartsChanged;
            ViewModel.OnNumberOfDrawingsChanged += ViewModel_OnNumberOfDrawingsChanged;
            ViewModel.OnItemBeingFixed += ViewModel_OnItemBeingFixed;
            ViewModel.EventLogged += ViewModel_EventLogged;
        }

        private void ViewModel_EventLogged(object sender, string e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                txbLog.Text += e + "\n";
                txbLog.ScrollToEnd();
            });
        }

        private void ViewModel_OnItemBeingFixed(object sender, (int assemblyIndex, string currentItem) e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                prBar.Value = (100.0 * e.assemblyIndex / (_totalNumberOfDrawings + _totalNumberOfAssemblies));
                lblCurrent.Content = e.currentItem;
            });
        }

        private void ViewModel_OnNumberOfDrawingsChanged(object sender, int e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                lblNumberOfDrawings.Content = e.ToString();
                _totalNumberOfDrawings = e;
            });
        }

        private void ViewModel_OnNumberOfPartsChanged(object sender, int e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                lblNumberOfParts.Content = e.ToString();
                _totalNumberOfParts = e;
            });
        }

        private void ViewModel_OnNumberOfAssembliesChanged(object sender, int e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                lblNumberOfAssemblies.Content = e.ToString();
                _totalNumberOfAssemblies = e;
            });
        }

        private async void Button_StartFixing_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                txbLog.Text = string.Empty;
                lblCurrent.Content = string.Empty;
                ViewModel.IsRunning = true;
                string basePattern = null;
                string patternReplace = null;
                List<string> fileNamesToInclude = null;
                if (!string.IsNullOrWhiteSpace(txbBasePattern.Text))
                {
                    basePattern = txbBasePattern.Text;
                }
                if (!string.IsNullOrWhiteSpace(txbReplace.Text))
                {
                    patternReplace = txbReplace.Text;
                }
                if (!string.IsNullOrWhiteSpace(txbJustInclude.Text))
                {
                    fileNamesToInclude = txbJustInclude.Text.Split(',').ToList();
                }
                var softwareType = (SoftwareType)((ComboBoxItem)cmbSoftwareType.SelectedItem).Tag;
                var extensionTypes = (FileExtensionTypes)((ComboBoxItem)cmbExtensionType.SelectedItem).Tag;
                var site = ((ComboBoxItem)cmbSites.SelectedItem)?.Content?.ToString() ?? "All Sites";
                await ViewModel.StartFixing(softwareType, fileNamesToInclude, basePattern, patternReplace, extensionTypes, site);
            }
            finally
            {
                ViewModel.IsRunning = false;         // re‑enable the button
            }
        }

        private async void Button_Test_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string targetFile = null;
                Dictionary<string, string> fileNamesToInclude = new Dictionary<string, string>();
                if (!string.IsNullOrWhiteSpace(txbCatiaTargetFile.Text))
                {
                    targetFile = txbCatiaTargetFile.Text;
                }
                if (!string.IsNullOrWhiteSpace(txbSourceFiles.Text))
                {
                    var hh = txbSourceFiles.Text.Trim().Split(',').ToList();
                    foreach (var h in hh)
                    {
                        if (!string.IsNullOrWhiteSpace(h))
                        {
                            var parts = h.Trim().Split(';');
                            if (parts.Length > 1)
                            {
                                var name = parts[0].Trim();
                                var newAddress = parts[1].Trim();
                                fileNamesToInclude.Add(name, newAddress);
                            }
                        }
                    }
                }
                MessageBox.Show("Dictionary contains: " + fileNamesToInclude.Count + " new addresses");

                await ViewModel.CatiaTest(targetFile, fileNamesToInclude);
            }
            finally
            {
                ViewModel.IsRunning = false;         // re‑enable the button
            }
        }

        private async void Button_FixCATIADrawing_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                txbLog.Text = string.Empty;
                ViewModel.IsRunning = true;
                string basePattern = null;
                string patternReplace = null;
                List<string> fileNamesToInclude = null;
                if (!string.IsNullOrWhiteSpace(txbBasePattern.Text))
                {
                    basePattern = txbBasePattern.Text;
                }
                if (!string.IsNullOrWhiteSpace(txbReplace.Text))
                {
                    patternReplace = txbReplace.Text;
                }
                if (!string.IsNullOrWhiteSpace(txbJustInclude.Text))
                {
                    fileNamesToInclude = txbJustInclude.Text.Split(',').ToList();
                }
                var site = ((ComboBoxItem)cmbSites.SelectedItem)?.Content?.ToString() ?? "All Sites";
                await ViewModel.StartFixing(Lib.SoftwareType.Catia, fileNamesToInclude, basePattern, patternReplace,Lib.FileExtensionTypes.Drawing, site);
            }
            finally
            {
                ViewModel.IsRunning = false;         // re‑enable the button
            }
        }

        private async void Button_FillSiteUUID_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                txbLog.Text = string.Empty;
                ViewModel.IsRunning = true;
                var site = ((ComboBoxItem)cmbSites.SelectedItem)?.Content?.ToString() ?? "All Sites";
                await ViewModel.FillSiteUUID(site);
            }
            finally
            {
                ViewModel.IsRunning = false;
            }
        }
    }
}
