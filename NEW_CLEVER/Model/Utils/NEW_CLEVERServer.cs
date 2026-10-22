using NEW_CLEVER.View.Dialogs;
using NEW_CLEVER.ViewModel;
using System;
using System.IO;
using System.IO.Pipes;

namespace NEW_CLEVER.Model.Utils
{
    class NEW_CLEVERServer
    {
        System.ComponentModel.BackgroundWorker worker;
        string path;
        NamedPipeServerStream pipeServer;

        public NEW_CLEVERServer()
        {
            path = "";
            worker = new System.ComponentModel.BackgroundWorker();
            worker.DoWork += Worker_DoWork;
            worker.RunWorkerCompleted += Worker_RunWorkerCompleted;
            pipeServer = new NamedPipeServerStream("NEW_CLEVER_pipe");
        }

        public void Start()
        {
            try
            {
                worker.RunWorkerAsync();
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
        }

        private void Worker_RunWorkerCompleted(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e)
        {
            try
            {
                using (StreamReader sr = new StreamReader(pipeServer))
                {
                    path = sr.ReadLine().Trim();
                }
                if (pipeServer.IsConnected)
                    pipeServer.Disconnect();
                if (path != "")
                    MainWindowVM.FileNew(path, true);
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
        }

        private void Worker_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
        {
            try
            {
                pipeServer.WaitForConnection();
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
        }

        private void ShowException(Exception ex)
        {
            var message = new MessageWindow(ex.Message);
            message.ShowDialog();
        }
    }
}
