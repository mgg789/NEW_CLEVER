using Clever.ViewModel.BaseVM;
using System.Windows;
using System.Windows.Media;

namespace Clever.ViewModel.PanelsVM
{
    internal class GitGraphSegmentVM : BaseViewModel
    {
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public double StrokeThickness { get; set; }
        public Brush Stroke { get; set; }
        public PointCollection Points { get; set; }
    }
}
