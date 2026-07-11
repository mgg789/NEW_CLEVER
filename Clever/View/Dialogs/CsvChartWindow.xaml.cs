using Clever.Model.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Clever.View.Dialogs
{
    public partial class CsvChartWindow : Window
    {
        private static readonly Color[] SeriesColors =
        {
            Color.FromRgb(10, 134, 255),
            Color.FromRgb(214, 87, 95),
            Color.FromRgb(79, 175, 91),
            Color.FromRgb(185, 133, 66),
            Color.FromRgb(133, 88, 208),
            Color.FromRgb(53, 162, 198),
            Color.FromRgb(209, 95, 145)
        };

        private readonly CsvChartData _data;

        internal CsvChartWindow(CsvChartData data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            InitializeComponent();
            chartTitle.Text = GetChartTitle(_data.Kind);
            Title = chartTitle.Text;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Redraw();
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Redraw();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Redraw()
        {
            if (chartCanvas == null || chartCanvas.ActualWidth < 80 || chartCanvas.ActualHeight < 80)
                return;

            chartCanvas.Children.Clear();
            if (_data.Kind == CsvChartKind.Pie)
                DrawPie();
            else
                DrawCartesianChart();
        }

        private void DrawCartesianChart()
        {
            var allPoints = _data.Series.SelectMany(series => series.Points).ToList();
            if (allPoints.Count == 0)
            {
                AddText("Нет числовых данных для построения.", 20, 20, 14, Brushes.Gray);
                return;
            }

            const double left = 70;
            const double top = 34;
            const double right = 24;
            const double bottom = 72;
            var width = Math.Max(1, chartCanvas.ActualWidth - left - right);
            var height = Math.Max(1, chartCanvas.ActualHeight - top - bottom);
            var minX = allPoints.Min(point => point.X);
            var maxX = allPoints.Max(point => point.X);
            var minY = Math.Min(0, allPoints.Min(point => point.Y));
            var maxY = Math.Max(0, allPoints.Max(point => point.Y));
            if (Math.Abs(maxX - minX) < double.Epsilon) { minX -= 1; maxX += 1; }
            if (Math.Abs(maxY - minY) < double.Epsilon) { minY -= 1; maxY += 1; }

            Func<double, double> xToCanvas = value => left + (value - minX) / (maxX - minX) * width;
            Func<double, double> yToCanvas = value => top + (maxY - value) / (maxY - minY) * height;
            DrawGrid(left, top, width, height, minX, maxX, minY, maxY, xToCanvas, yToCanvas, allPoints);

            for (var seriesIndex = 0; seriesIndex < _data.Series.Count; seriesIndex++)
            {
                var series = _data.Series[seriesIndex];
                var brush = new SolidColorBrush(SeriesColors[seriesIndex % SeriesColors.Length]);
                brush.Freeze();
                if (_data.Kind == CsvChartKind.Line && series.Points.Count > 1)
                {
                    var polyline = new Polyline { Stroke = brush, StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round };
                    foreach (var point in series.Points.OrderBy(point => point.X))
                        polyline.Points.Add(new Point(xToCanvas(point.X), yToCanvas(point.Y)));
                    chartCanvas.Children.Add(polyline);
                }

                foreach (var point in series.Points)
                {
                    var marker = new Ellipse
                    {
                        Width = _data.Kind == CsvChartKind.Line ? 7 : 8,
                        Height = _data.Kind == CsvChartKind.Line ? 7 : 8,
                        Fill = brush,
                        Stroke = Brushes.White,
                        StrokeThickness = 1.2,
                        ToolTip = BuildPointToolTip(series.Name, point)
                    };
                    Canvas.SetLeft(marker, xToCanvas(point.X) - marker.Width / 2);
                    Canvas.SetTop(marker, yToCanvas(point.Y) - marker.Height / 2);
                    chartCanvas.Children.Add(marker);
                }
                DrawLegend(series.Name, brush, seriesIndex);
            }

            AddText(_data.XAxisTitle ?? "", left + width / 2 - 55, top + height + 45, 11, Brushes.DimGray);
            var yTitle = AddText(_data.YAxisTitle ?? "", 7, top + height / 2, 11, Brushes.DimGray);
            yTitle.RenderTransform = new RotateTransform(-90);
            yTitle.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        private void DrawGrid(double left, double top, double width, double height, double minX, double maxX, double minY, double maxY, Func<double, double> xToCanvas, Func<double, double> yToCanvas, IList<CsvChartPoint> allPoints)
        {
            var gridBrush = new SolidColorBrush(Color.FromArgb(44, 100, 116, 139));
            gridBrush.Freeze();
            var axisBrush = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            axisBrush.Freeze();
            for (var index = 0; index <= 5; index++)
            {
                var fraction = index / 5d;
                var x = left + width * fraction;
                var y = top + height * fraction;
                AddLine(x, top, x, top + height, gridBrush, 1);
                AddLine(left, y, left + width, y, gridBrush, 1);

                var xValue = minX + (maxX - minX) * fraction;
                var xLabel = FormatNumber(xValue);
                if (_data.Kind == CsvChartKind.Line)
                {
                    var closestPoint = allPoints.OrderBy(point => Math.Abs(point.X - xValue)).FirstOrDefault();
                    if (closestPoint != null && Math.Abs(closestPoint.X - xValue) < 0.35)
                        xLabel = closestPoint.Label;
                }
                AddText(TrimLabel(xLabel, 14), x - 26, top + height + 8, 10, Brushes.Gray);
                var yValue = maxY - (maxY - minY) * fraction;
                AddText(FormatNumber(yValue), 8, y - 7, 10, Brushes.Gray);
            }

            AddLine(left, top, left, top + height, axisBrush, 1.4);
            AddLine(left, top + height, left + width, top + height, axisBrush, 1.4);
        }

        private void DrawPie()
        {
            if (_data.Slices.Count == 0)
            {
                AddText("Нет положительных числовых данных для круговой диаграммы.", 20, 20, 14, Brushes.Gray);
                return;
            }

            var center = new Point(chartCanvas.ActualWidth * 0.42, chartCanvas.ActualHeight * 0.53);
            var radius = Math.Max(30, Math.Min(chartCanvas.ActualWidth * 0.28, chartCanvas.ActualHeight * 0.34));
            var total = _data.Slices.Sum(slice => slice.Value);
            if (_data.Slices.Count == 1)
            {
                var slice = _data.Slices[0];
                var brush = new SolidColorBrush(SeriesColors[0]);
                brush.Freeze();
                var circle = new Ellipse
                {
                    Width = radius * 2,
                    Height = radius * 2,
                    Fill = brush,
                    Stroke = Brushes.White,
                    StrokeThickness = 1.2,
                    ToolTip = slice.Label + "\nЗначение: " + FormatNumber(slice.Value) + "\nДоля: 100%"
                };
                Canvas.SetLeft(circle, center.X - radius);
                Canvas.SetTop(circle, center.Y - radius);
                chartCanvas.Children.Add(circle);
                DrawLegend(slice.Label + ": " + FormatNumber(slice.Value), brush, 0);
                return;
            }

            var start = -90d;
            for (var index = 0; index < _data.Slices.Count; index++)
            {
                var slice = _data.Slices[index];
                var sweep = slice.Value / total * 360d;
                var brush = new SolidColorBrush(SeriesColors[index % SeriesColors.Length]);
                brush.Freeze();
                var path = new Path
                {
                    Data = CreatePieGeometry(center, radius, start, sweep),
                    Fill = brush,
                    Stroke = Brushes.White,
                    StrokeThickness = 1.2,
                    ToolTip = slice.Label + "\nЗначение: " + FormatNumber(slice.Value) + "\nДоля: " + FormatNumber(slice.Value / total * 100) + "%"
                };
                chartCanvas.Children.Add(path);
                DrawLegend(slice.Label + ": " + FormatNumber(slice.Value), brush, index);
                start += sweep;
            }
        }

        private Geometry CreatePieGeometry(Point center, double radius, double startDegrees, double sweepDegrees)
        {
            var startRadians = startDegrees * Math.PI / 180d;
            var endRadians = (startDegrees + sweepDegrees) * Math.PI / 180d;
            var startPoint = new Point(center.X + radius * Math.Cos(startRadians), center.Y + radius * Math.Sin(startRadians));
            var endPoint = new Point(center.X + radius * Math.Cos(endRadians), center.Y + radius * Math.Sin(endRadians));
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(center, true, true);
                context.LineTo(startPoint, true, false);
                context.ArcTo(endPoint, new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            return geometry;
        }

        private void DrawLegend(string text, Brush brush, int index)
        {
            var x = chartCanvas.ActualWidth * 0.74;
            var y = 24 + index * 22;
            var indicator = new Rectangle { Width = 11, Height = 11, RadiusX = 2, RadiusY = 2, Fill = brush };
            Canvas.SetLeft(indicator, x);
            Canvas.SetTop(indicator, y + 2);
            chartCanvas.Children.Add(indicator);
            AddText(TrimLabel(text, 23), x + 16, y, 10.5, Brushes.DimGray);
        }

        private TextBlock AddText(string text, double x, double y, double size, Brush foreground)
        {
            var block = new TextBlock { Text = text ?? "", FontSize = size, Foreground = foreground, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 };
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, y);
            chartCanvas.Children.Add(block);
            return block;
        }

        private void AddLine(double x1, double y1, double x2, double y2, Brush brush, double thickness)
        {
            chartCanvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness });
        }

        private static string BuildPointToolTip(string seriesName, CsvChartPoint point)
        {
            var source = point.Source;
            return (seriesName ?? "") + "\n"
                + (point.Label ?? "") + "\n"
                + "Столбец: " + (source == null ? "" : source.ColumnName) + "\n"
                + "Значение: " + FormatNumber(point.Y);
        }

        private static string GetChartTitle(CsvChartKind kind)
        {
            switch (kind)
            {
                case CsvChartKind.Distribution: return "Распределение CSV";
                case CsvChartKind.Pie: return "Круговая диаграмма CSV";
                default: return "График CSV";
            }
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("0.###", CultureInfo.CurrentCulture);
        }

        private static string TrimLabel(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value ?? "";
            return value.Substring(0, Math.Max(1, maxLength - 1)) + "…";
        }
    }
}
