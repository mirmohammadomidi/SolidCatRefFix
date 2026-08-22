using SolidRefrenceRename.WPFUI.Lib;
using SolidRefrenceRename.WPFUI.Lib.Config;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace SolidRefrenceRename.WPFUI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static string DefaultConnectionString { get; private set; }
        public static string AppSettingsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\IFSDocumentRenamingUtil";
        public static string GeneralConfigFileAddress = AppSettingsDirectory + "\\generalConfig.json";
        public App()
        {
            if (!Directory.Exists(AppSettingsDirectory))
            {
                Directory.CreateDirectory(AppSettingsDirectory);
            }
        }
        protected override async void OnStartup(StartupEventArgs e)
        {
            ConfigService cobfigurations = new ConfigService();
            if (!File.Exists(GeneralConfigFileAddress))
            {
                MessageBox.Show("ارتباط با پایگاه داده اصلی سیستم یافت نشد. لطفا ابتدا پایگاه داده را مشخص کنید", "تایید", MessageBoxButton.OK);                 
            }
            else
            {
                var gc = cobfigurations.ReadFromFile(GeneralConfigFileAddress);
                if (gc == null)
                {
                    gc = new GeneralConfig()
                    {
                        ManagementConnectionString = "Server='.';Data Source='IFSCodeCleansing';Integrated Security=true",
                    };
                    cobfigurations.SaveToFile(gc, GeneralConfigFileAddress);
                }
                DefaultConnectionString = gc.ManagementConnectionString;              

            }
        }
    }
}
