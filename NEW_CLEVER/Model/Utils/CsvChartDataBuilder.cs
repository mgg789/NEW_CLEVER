using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NEW_CLEVER.Model.Utils
{
    internal enum CsvChartKind
    {
        Line,
        Distribution,
        Pie
    }

    internal enum CsvChartLineOrientation
    {
        Auto,
        SeriesByColumn,
        SeriesByRow
    }

    internal sealed class CsvChartCell
    {
        internal CsvChartCell(int rowIndex, int columnIndex, string columnName, string value)
        {
            RowIndex = rowIndex;
            ColumnIndex = columnIndex;
            ColumnName = columnName ?? "";
            Value = value ?? "";
        }

        internal int RowIndex { get; private set; }
        internal int ColumnIndex { get; private set; }
        internal string ColumnName { get; private set; }
        internal string Value { get; private set; }
    }

    internal sealed class CsvChartPoint
    {
        internal CsvChartPoint(double x, double y, string label, CsvChartCell source)
        {
            X = x;
            Y = y;
            Label = label ?? "";
            Source = source;
        }

        internal double X { get; private set; }
        internal double Y { get; private set; }
        internal string Label { get; private set; }
        internal CsvChartCell Source { get; private set; }
    }

    internal sealed class CsvChartSeries
    {
        internal CsvChartSeries(string name)
        {
            Name = name ?? "";
            Points = new List<CsvChartPoint>();
        }

        internal string Name { get; private set; }
        internal IList<CsvChartPoint> Points { get; private set; }
    }

    internal sealed class CsvChartSlice
    {
        internal CsvChartSlice(string label, double value, CsvChartCell source)
        {
            Label = label ?? "";
            Value = value;
            Source = source;
        }

        internal string Label { get; private set; }
        internal double Value { get; private set; }
        internal CsvChartCell Source { get; private set; }
    }

    internal sealed class CsvChartData
    {
        internal CsvChartData(CsvChartKind kind)
        {
            Kind = kind;
            Series = new List<CsvChartSeries>();
            Slices = new List<CsvChartSlice>();
        }

        internal CsvChartKind Kind { get; private set; }
        internal IList<CsvChartSeries> Series { get; private set; }
        internal IList<CsvChartSlice> Slices { get; private set; }
        internal string XAxisTitle { get; set; }
        internal string YAxisTitle { get; set; }
        internal bool HasData { get { return Series.Any(series => series.Points.Count > 0) || Slices.Count > 0; } }
    }

    internal static class CsvChartDataBuilder
    {
        internal static CsvChartData Build(IEnumerable<CsvChartCell> selectedCells, CsvChartKind kind, CsvChartLineOrientation lineOrientation)
        {
            var numericCells = (selectedCells ?? Enumerable.Empty<CsvChartCell>())
                .Where(cell => cell != null)
                .Select(cell => new { Cell = cell, Value = TryParseNumber(cell.Value) })
                .Where(item => item.Value.HasValue)
                .OrderBy(item => item.Cell.RowIndex)
                .ThenBy(item => item.Cell.ColumnIndex)
                .ToList();

            var data = new CsvChartData(kind);
            if (kind == CsvChartKind.Pie)
            {
                foreach (var item in numericCells.Where(item => item.Value.Value > 0))
                {
                    var label = item.Cell.ColumnName + ", строка " + (item.Cell.RowIndex + 1).ToString(CultureInfo.InvariantCulture);
                    data.Slices.Add(new CsvChartSlice(label, item.Value.Value, item.Cell));
                }
                return data;
            }

            if (kind == CsvChartKind.Distribution)
            {
                var seriesByColumn = new Dictionary<int, CsvChartSeries>();
                var ordinal = 1;
                foreach (var item in numericCells)
                {
                    CsvChartSeries series;
                    if (!seriesByColumn.TryGetValue(item.Cell.ColumnIndex, out series))
                    {
                        series = new CsvChartSeries(item.Cell.ColumnName);
                        seriesByColumn.Add(item.Cell.ColumnIndex, series);
                        data.Series.Add(series);
                    }
                    var label = item.Cell.ColumnName + ", строка " + (item.Cell.RowIndex + 1).ToString(CultureInfo.InvariantCulture);
                    series.Points.Add(new CsvChartPoint(ordinal++, item.Value.Value, label, item.Cell));
                }
                data.XAxisTitle = "Порядок выбранных значений";
                data.YAxisTitle = "Значение";
                return data;
            }

            var orientation = ResolveLineOrientation(numericCells.Select(item => item.Cell), lineOrientation);
            if (orientation == CsvChartLineOrientation.SeriesByRow)
            {
                foreach (var group in numericCells.GroupBy(item => item.Cell.RowIndex).OrderBy(group => group.Key))
                {
                    var series = new CsvChartSeries("Строка " + (group.Key + 1).ToString(CultureInfo.InvariantCulture));
                    foreach (var item in group.OrderBy(item => item.Cell.ColumnIndex))
                        series.Points.Add(new CsvChartPoint(item.Cell.ColumnIndex + 1, item.Value.Value, item.Cell.ColumnName, item.Cell));
                    data.Series.Add(series);
                }
                data.XAxisTitle = "Столбцы";
            }
            else
            {
                foreach (var group in numericCells.GroupBy(item => new { item.Cell.ColumnIndex, item.Cell.ColumnName }).OrderBy(group => group.Key.ColumnIndex))
                {
                    var series = new CsvChartSeries(group.Key.ColumnName);
                    foreach (var item in group.OrderBy(item => item.Cell.RowIndex))
                    {
                        var label = "Строка " + (item.Cell.RowIndex + 1).ToString(CultureInfo.InvariantCulture);
                        series.Points.Add(new CsvChartPoint(item.Cell.RowIndex + 1, item.Value.Value, label, item.Cell));
                    }
                    data.Series.Add(series);
                }
                data.XAxisTitle = "Строки";
            }

            data.YAxisTitle = "Значение";
            return data;
        }

        private static CsvChartLineOrientation ResolveLineOrientation(IEnumerable<CsvChartCell> cells, CsvChartLineOrientation requested)
        {
            if (requested != CsvChartLineOrientation.Auto)
                return requested;

            var source = (cells ?? Enumerable.Empty<CsvChartCell>()).ToList();
            var rowCount = source.Select(cell => cell.RowIndex).Distinct().Count();
            var columnCount = source.Select(cell => cell.ColumnIndex).Distinct().Count();
            return rowCount < columnCount ? CsvChartLineOrientation.SeriesByRow : CsvChartLineOrientation.SeriesByColumn;
        }

        private static double? TryParseNumber(string value)
        {
            double number;
            if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out number)
                || double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out number))
                return !double.IsNaN(number) && !double.IsInfinity(number) ? number : (double?)null;
            return null;
        }
    }
}
