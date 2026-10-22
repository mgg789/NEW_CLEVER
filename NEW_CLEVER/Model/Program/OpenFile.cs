using NEW_CLEVER.Model.Bplus;
using NEW_CLEVER.Model.Bplus.BPInterpreter;
using NEW_CLEVER.Model.Intellisense;
using NEW_CLEVER.Model.Utils;
using NEW_CLEVER.ViewModel;
using Microsoft.Win32;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace NEW_CLEVER.Model.Program
{
    class OpenFile
    {
        public string Open(string path, out string fullPath, out string directory, out string name)
        {
            string text = "";
            FileInfo info;
            OpenFileDialog open = new OpenFileDialog();
            open.Filter = "NEW_CLEVER editor files (*.bp;*.bpm;*.bpi;*.md;*.txt;*.csv;*.ms)|*.bp;*.bpm;*.bpi;*.md;*.txt;*.csv;*.ms";
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                open.InitialDirectory = Path.GetDirectoryName(path);
            else if (!string.IsNullOrWhiteSpace(MainWindowVM.CurrentProjectRoot) && Directory.Exists(MainWindowVM.CurrentProjectRoot))
                open.InitialDirectory = MainWindowVM.CurrentProjectRoot;
            if (open.ShowDialog() == true)
            {
                text = File.ReadAllText(open.FileName);
                info = new FileInfo(open.FileName);
                fullPath = info.FullName;
                directory = info.DirectoryName;
                name = info.Name;
            }
            else
            {
                text = null;
                fullPath = null;
                directory = null;
                name = null;
            }
            return text;
        }

        internal static void IncludeFileOpen(ProgramMap map, Dictionary<string, ProgramData> data, HashSet<string> namesI)
        {
            var includes = new Dictionary<string, Include>();

            foreach (var inc in map.Includes)
            {
                if (!namesI.Contains(inc.Value.OriginName))
                {
                    namesI.Add(inc.Value.OriginName);
                    includes.Add(inc.Key, inc.Value);
                }
            }

            foreach (var inc in includes)
            {
                var name = inc.Value.OriginName;
                var path = inc.Value.Path + name;

                if (File.Exists(path))
                {
                    if (data.ContainsKey(name))
                    {
                        if (path != data[name].FullPath)
                        {
                            data.Remove(name);
                        }
                    }

                    if (!data.ContainsKey(name))
                    {
                        var text = File.ReadAllText(path);
                        var item = new NewTabItem().Create(text, name);
                        var pd = new ProgramData();
                        pd.ParseName = name;
                        pd.ClosedName = name;
                        pd.OldName = name;
                        pd.Name = name;
                        pd.AddToObjects = false;
                        pd.Item = item;
                        pd.Path = inc.Value.Path;
                        pd.FullPath = path;
                        pd.TextChange = false;
                        data.Add(name, pd);
                        //IntellisenseParser.SetVSLSync(pd, data);
                    }
                }
                else
                {
                    if (data.ContainsKey(name))
                    {
                        data.Remove(name);
                    }

                    map.Includes.Remove(name.ToLower());
                }
            }
        }

        internal static void ImportFileOpen(ProgramMap map, Dictionary<string, ProgramData> data, HashSet<string> namesM)
        {
            var imports = new Dictionary<string, Module>();

            foreach (var imp in map.Imports)
            {
                if (!namesM.Contains(imp.Value.OriginName))
                {
                    namesM.Add(imp.Value.OriginName);
                    imports.Add(imp.Key, imp.Value);
                }
            }
            foreach (var imp in imports)
            {
                var name = imp.Value.OriginName;
                var path = imp.Value.Path + name;

                if (File.Exists(path))
                {
                    if (data.ContainsKey(name))
                    {
                        if (path != data[name].FullPath)
                        {
                            data.Remove(name);
                        }
                    }

                    if (!data.ContainsKey(name))
                    {
                        var text = File.ReadAllText(path);
                        var item = new NewTabItem().Create(text, name);
                        var pd = new ProgramData();
                        pd.ParseName = name;
                        pd.ClosedName = name;
                        pd.OldName = name;
                        pd.Name = name;
                        pd.AddToObjects = false;
                        pd.Item = item;
                        pd.Path = imp.Value.Path;
                        pd.FullPath = path;
                        pd.TextChange = false;
                        data.Add(name, pd);
                        //IntellisenseParser.SetVSLSync(pd, data);
                    }
                }
                else
                {
                    if (data.ContainsKey(name))
                    {
                        data.Remove(name);
                    }

                    map.Imports.Remove(name.ToLower());
                }
            }
        }

        internal static void MainFileOpen(ProgramMap map, Dictionary<string, ProgramData> data)
        {
            var name = map.MainName;
            var path = map.MainPath + name;

            if (File.Exists(path))
            {
                if (data.ContainsKey(name))
                {
                    if (path != data[name].FullPath)
                    {
                        CommonData.Status.Add(path + "  " + data[name].FullPath);
                        data.Remove(name);
                    }
                }

                if (!data.ContainsKey(name))
                {
                    var text = File.ReadAllText(path);
                    var item = new NewTabItem().Create(text, name);
                    var pd = new ProgramData();
                    pd.ParseName = name;
                    pd.ClosedName = name;
                    pd.OldName = name;
                    pd.Name = name;
                    pd.AddToObjects = false;
                    pd.Item = item;
                    pd.Path = map.MainPath;
                    pd.FullPath = path;
                    pd.TextChange = false;
                    data.Add(name, pd);
                    //IntellisenseParser.SetVSLSync(pd, data);
                }
            }
            else
            {
                if (data.ContainsKey(name))
                {
                    data.Remove(name);
                }

                map.MainName = "";
            }
        }
    }
}
