using SolidRefrenceRename.WPFUI.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SolidRefrenceRename.WPFUI
{
    /// <summary>
    /// Batch processing of CATProduct files.  Same layout as
    /// <see cref="ReferenceFixWindow"/> except that no file type is selected: every
    /// CATProduct of the site is staged and fixed in one batch run.
    /// </summary>
    public partial class BatchProductWindow : Window
    {
        private int _totalNumberOfAssemblies;
        public SolidFixViewModel ViewModel => (SolidFixViewModel)this.DataContext;
        public BatchProductWindow()
        {
            DataContext = new SolidFixViewModel();
            InitializeComponent();
            ViewModel.OnNumberOfAssembliesChanged += ViewModel_OnNumberOfAssembliesChanged;
            ViewModel.OnNumberOfPartsChanged += ViewModel_OnNumberOfPartsChanged;
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
                if (_totalNumberOfAssemblies > 0)
                {
                    prBar.Value = (100.0 * e.assemblyIndex / _totalNumberOfAssemblies);
                }
                lblCurrent.Content = e.currentItem;
            });
        }

        private void ViewModel_OnNumberOfPartsChanged(object sender, int e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                lblNumberOfParts.Content = e.ToString();
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

        private async void Button_StartBatchFixing_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                txbLog.Text = string.Empty;
                lblCurrent.Content = string.Empty;
                prBar.Value = 0;
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
                await ViewModel.StartFixingCatiaProductsBatch(fileNamesToInclude, basePattern, patternReplace, site);
            }
            finally
            {
                ViewModel.IsRunning = false;         // re-enable the button
            }
        }
    }
}
