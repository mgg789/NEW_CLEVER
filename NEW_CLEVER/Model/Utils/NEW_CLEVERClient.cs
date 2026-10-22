using System.IO;
using System.IO.Pipes;

namespace NEW_CLEVER.Model.Utils
{
    class NEW_CLEVERClient
    {
        string _path = "";

        public NEW_CLEVERClient(string path)
        {
            _path = path;
        }

        public void StartClient()
        {
            using (NamedPipeClientStream pipeClient = new NamedPipeClientStream("NEW_CLEVER_pipe"))
            {
                pipeClient.Connect();
                using (StreamWriter sw = new StreamWriter(pipeClient))
                {
                    sw.AutoFlush = true;
                    sw.WriteLine(_path);
                }
            }
        }
    }
}
