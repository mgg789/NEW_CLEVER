using NEW_CLEVER.CommonData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace NEW_CLEVER.Model.Utils
{
    class Localizator
    {
        public Dictionary<string, string> ReadInterface()
        {
            string loc = Configurations.Get.Language;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Localization", loc, "interface_" + loc + ".txt");
            var dictionary = new Dictionary<string, string>();
            var lines = File.ReadAllLines(path);
            foreach (var line in lines)
            {
                if (line != "")
                {
                    var tmp = line.Split(new char[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                    dictionary.Add(tmp[0].Trim(), tmp[1].Trim());
                }
            }
            return dictionary;
        }
        public Dictionary<string, string> ReadErrors()
        {
            string loc = Configurations.Get.Language;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Localization", loc, "errors_" + loc + ".txt");
            var dictionary = new Dictionary<string, string>();
            var fi = new FileInfo(path);

            if (!fi.Exists)
            {
                return null;
            }

            var lines = File.ReadAllLines(path);
            foreach (var line in lines)
            {
                if (line != "")
                {
                    var tmp = line.Split(new char[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                    dictionary.Add(tmp[0].Trim(), tmp[1].Trim());
                }
            }
            return dictionary;
        }
    }
}
