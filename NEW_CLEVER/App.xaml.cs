using NEW_CLEVER.CommonData;
using NEW_CLEVER.Model.Bplus;
using NEW_CLEVER.Model.Agent;
using NEW_CLEVER.Model.Intellisense;
using NEW_CLEVER.Model.Utils;
using NEW_CLEVER.View.Dialogs;
using NEW_CLEVER.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

namespace NEW_CLEVER
{
    /// <summary>
    /// Логика взаимодействия для App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static Dictionary<string, string> Errors = new Dictionary<string, string>();
        public static ObservableCollection<IntellisenseObject> IntellisenseObjects;
        Localizator localizator = new Localizator();

        public App()
        {
            Exit += (sender, args) =>
            {
                if (NEW_CLEVERAgentServices.Current != null)
                    NEW_CLEVERAgentServices.Current.Dispose();
                PrivateFontLoader.UnloadBundledFonts();
            };
        }

        void Aplication_StartUp(object sender, StartupEventArgs e)
        {
            var prc = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName);

            if (prc.Length > 1)
            {
                try
                {
                    if (e.Args.Length >= 1)
                    {
                        var client = new NEW_CLEVERClient(e.Args[0]);
                        client.StartClient();
                    }
                    this.Shutdown();
                }
                catch (Exception ex)
                {
                    var message = new MessageWindow(ex.Message);
                    message.ShowDialog();
                    this.Shutdown();
                }
            }
            else
            {
                try
                {
                    var server = new NEW_CLEVERServer();
                    server.Start();
                    PrivateFontLoader.LoadBundledFonts();
                    Configurations.Install();
                    GUILanguage.Install();
                    NEW_CLEVERAgentServices.Current.InitializeDefaultFoundation();
                    BpColors.Install();
                    Errors = localizator.ReadErrors();
                    IntellisenseObjects = new ReadXml().Read();     
                    MainWindow main = new MainWindow();
                    main.Show();
                    main.UpdateGridSize();
                    NEW_CLEVERAgentServices.Current.StartSessionBackend();

                    //new ValidData().GetData();

                    if (e.Args.Length > 0)
                    {
                        MainWindowVM.FileNew(e.Args[0], false);
                    }


                    if (!Configurations.Get.Association && !Configurations.Get.Association_NotShow)
                    {
                        var reg = new FirstAssociationWindow();
                        if (reg.ShowDialog() == true)
                        {
                            var result = new KeyRegistry("r");
                            Configurations.Get.Association = result.IsRegistry;
                        }
                    }

                }
                catch (Exception ex)
                {
                    var message = new MessageWindow(ex.Message);
                    message.ShowDialog();
                    this.Shutdown();
                }
            }
        }
    }
}
