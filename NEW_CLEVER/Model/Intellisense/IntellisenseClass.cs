using System.Collections.Generic;

namespace NEW_CLEVER.Model.Intellisense
{
    public class IntellisenseClass
    {
        public IntellisenseClass()
        {
            Type = IntellisenseType.None;
            Name = "";
            Summary = new List<string>();
        }
        public IntellisenseType Type { get; set; }
        public string Name { get; set; }
        public List<string> Summary { get; set; }
    }
}
