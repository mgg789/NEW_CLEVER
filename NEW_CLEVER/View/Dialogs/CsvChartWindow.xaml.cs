using NEW_CLEVER.Model.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace NEW_CLEVER.View.Dialogs
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
        private readonly List<CsvChartHoverTarget> _hoverTargets = new List<CsvChartHoverTarget>();
        private Border _hoverPanel;

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

        private void Redraw()
        {
            if (chartCanvas == null || chartCanvas.ActualWidth < 80 || chartCanvas.ActualHeight < 80)
                return;

            chartCanvas.Children.Clear();
            legendPanel.Children.Clear();
            _hoverTargets.Clear();
            _hoverPanel = null;
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
                    var orderedPoints = series.Points.OrderBy(point => point.X).ToList();
                    foreach (var point in orderedPoints)
                        polyline.Points.Add(new Point(xToCanvas(point.X), yToCanvas(point.Y)));
                    chartCanvas.Children.Add(polyline);
                    for (var pointIndex = 1; pointIndex < orderedPoints.Count; pointIndex++)
                    {
                        var previous = orderedPoints[pointIndex - 1];
                        var current = orderedPoints[pointIndex];
                        AddHoverSegment(
                            new Point(xToCanvas(previous.X), yToCanvas(previous.Y)),
                            new Point(xToCanvas(current.X), yToCanvas(current.Y)),
                            BuildPointToolTip(series.Name, current));
                    }
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
                    AddHoverPoint(new Point(xToCanvas(point.X), yToCanvas(point.Y)), BuildPointToolTip(series.Name, point));
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

            var center = new Point(chartCanvas.ActualWidth * 0.5, chartCanvas.ActualHeight * 0.53);
            var radius = Math.Max(30, Math.Min(chartCanvas.ActualWidth * 0.32, chartCanvas.ActualHeight * 0.34));
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
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, index == 0 ? 2 : 8, 0, 0) };
            row.Children.Add(new Rectangle { Width = 11, Height = 11, RadiusX = 2, RadiusY = 2, Fill = brush, Margin = new Thickness(0, 2, 7, 0) });
            row.Children.Add(new TextBlock { Text = TrimLabel(text, 22), FontSize = 10.5, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
            legendPanel.Children.Add(row);
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

        private void AddHoverPoint(Point point, string text)
        {
            _hoverTargets.Add(CsvChartHoverTarget.ForPoint(point, text));
        }

        private void AddHoverSegment(Point start, Point end, string text)
        {
            _hoverTargets.Add(CsvChartHoverTarget.ForSegment(start, end, text));
        }

        private void ChartCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pointer = e.GetPosition(chartCanvas);
            CsvChartHoverTarget closest = null;
            var closestDistance = double.MaxValue;
            foreach (var target in _hoverTargets)
            {
                var distance = target.DistanceTo(pointer);
                if (distance < closestDistance)
                {
                    closest = target;
                    closestDistance = distance;
                }
            }

            if (closest == null || closestDistance > 12)
            {
                HideChartHover();
                return;
            }

            ShowChartHover(closest.Text, pointer);
        }

        private void ChartCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            HideChartHover();
        }

        private void ShowChartHover(string text, Point pointer)
        {
            if (_hoverPanel == null)
            {
                var content = new TextBlock { Foreground = Brushes.White, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 250 };
                _hoverPanel = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(49, 57, 73)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6, 8, 6),
                    IsHitTestVisible = false,
                    Child = content
                };
                chartCanvas.Children.Add(_hoverPanel);
            }

            var textBlock = _hoverPanel.Child as TextBlock;
            if (textBlock != null)
                textBlock.Text = text ?? "";
            _hoverPanel.Visibility = Visibility.Visible;
            Canvas.SetLeft(_hoverPanel, Math.Max(4, Math.Min(pointer.X + 12, chartCanvas.ActualWidth - 265)));
            Canvas.SetTop(_hoverPanel, Math.Max(4, Math.Min(pointer.Y + 12, chartCanvas.ActualHeight - 90)));
        }

        private void HideChartHover()
        {
            if (_hoverPanel != null)
                _hoverPanel.Visibility = Visibility.Collapsed;
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

        private sealed class CsvChartHoverTarget
        {
            private readonly Point _start;
            private readonly Point _end;
            private readonly bool _isSegment;

            private CsvChartHoverTarget(Point start, Point end, bool isSegment, string text)
            {
                _start = start;
                _end = end;
                _isSegment = isSegment;
                Text = text ?? "";
            }

            internal string Text { get; private set; }

            internal static CsvChartHoverTarget ForPoint(Point point, string text)
            {
                return new CsvChartHoverTarget(point, point, false, text);
            }

            internal static CsvChartHoverTarget ForSegment(Point start, Point end, string text)
            {
                return new CsvChartHoverTarget(start, end, true, text);
            }

            internal double DistanceTo(Point point)
            {
                if (!_isSegment)
                    return (point - _start).Length;

                var segment = _end - _start;
                var segmentLengthSquared = segment.X * segment.X + segment.Y * segment.Y;
                if (segmentLengthSquared < double.Epsilon)
                    return (point - _start).Length;

                var projection = ((point.X - _start.X) * segment.X + (point.Y - _start.Y) * segment.Y) / segmentLengthSquared;
                projection = Math.Max(0, Math.Min(1, projection));
                var closest = new Point(_start.X + projection * segment.X, _start.Y + projection * segment.Y);
                return (point - closest).Length;
            }
        }
    }
}
