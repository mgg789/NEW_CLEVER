using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Clever.Model.Utils
{
    internal sealed class CsvTableChangeTracker : IDisposable
    {
        private DataTable _table;

        internal CsvTableChangeTracker(DataTable table)
        {
            _table = table ?? throw new ArgumentNullException(nameof(table));
            _table.ColumnChanged += Table_ColumnChanged;
            _table.RowChanged += Table_RowChanged;
            _table.RowDeleted += Table_RowDeleted;
        }

        internal event EventHandler Changed;

        public void Dispose()
        {
            var table = _table;
            if (table == null)
                return;

            _table = null;
            table.ColumnChanged -= Table_ColumnChanged;
            table.RowChanged -= Table_RowChanged;
            table.RowDeleted -= Table_RowDeleted;
        }

        private void Table_ColumnChanged(object sender, DataColumnChangeEventArgs e)
        {
            OnChanged();
        }

        private void Table_RowChanged(object sender, DataRowChangeEventArgs e)
        {
            OnChanged();
        }

        private void Table_RowDeleted(object sender, DataRowChangeEventArgs e)
        {
            OnChanged();
        }

        private void OnChanged()
        {
            var handler = Changed;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }
    }

    internal class CsvTableDocument
    {
        private CsvTableDocument()
        {
            Table = new DataTable();
        }

        internal DataTable Table { get; private set; }
        internal bool HasHeader { get; private set; }
        internal char Delimiter { get; private set; }

        internal static CsvTableDocument Parse(string text)
        {
            var document = new CsvTableDocument();
            document.Delimiter = DetectDelimiter(text);

            var rows = ParseRows(text ?? "", document.Delimiter);
            var columnCount = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(row => row.Count));
            document.HasHeader = DetectHeader(rows);

            var headerRow = document.HasHeader ? rows[0] : null;
            for (var i = 0; i < columnCount; i++)
            {
                var caption = document.HasHeader && i < headerRow.Count && !string.IsNullOrWhiteSpace(headerRow[i])
                    ? headerRow[i]
                    : GetExcelColumnName(i);

                var column = document.Table.Columns.Add("Column" + (i + 1).ToString(CultureInfo.InvariantCulture), typeof(string));
                column.Caption = caption;
            }

            var startRow = document.HasHeader ? 1 : 0;
            for (var i = startRow; i < rows.Count; i++)
            {
                var dataRow = document.Table.NewRow();
                for (var j = 0; j < columnCount; j++)
                    dataRow[j] = j < rows[i].Count ? rows[i][j] : "";

                document.Table.Rows.Add(dataRow);
            }

            return document;
        }

        internal string ToCsv()
        {
            var builder = new StringBuilder();
            if (HasHeader)
                AppendRow(builder, Table.Columns.Cast<DataColumn>().Select(column => column.Caption));

            foreach (DataRow row in Table.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                AppendRow(builder, Table.Columns.Cast<DataColumn>().Select(column => Convert.ToString(row[column], CultureInfo.InvariantCulture) ?? ""));
            }

            return builder.ToString();
        }

        private void AppendRow(StringBuilder builder, IEnumerable<string> fields)
        {
            if (builder.Length > 0)
                builder.Append(Environment.NewLine);

            var first = true;
            foreach (var field in fields)
            {
                if (!first)
                    builder.Append(Delimiter);

                builder.Append(EscapeField(field ?? ""));
                first = false;
            }
        }

        private string EscapeField(string field)
        {
            if (field.IndexOf(Delimiter) < 0 && field.IndexOf('"') < 0 && field.IndexOf('\r') < 0 && field.IndexOf('\n') < 0)
                return field;

            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        private static char DetectDelimiter(string text)
        {
            var candidates = new[] { ',', ';', '\t' };
            var lines = (text ?? "")
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Take(12)
                .ToList();

            if (lines.Count == 0)
                return ',';

            var best = ',';
            var bestScore = -1;
            foreach (var candidate in candidates)
            {
                var counts = lines.Select(line => ParseRows(line, candidate).FirstOrDefault()?.Count ?? 1).ToList();
                var max = counts.Max();
                var score = max * 100 - counts.Distinct().Count();
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best;
        }

        private static bool DetectHeader(IList<List<string>> rows)
        {
            if (rows == null || rows.Count < 2 || rows[0].Count == 0)
                return false;

            var first = rows[0];
            if (first.All(string.IsNullOrWhiteSpace))
                return false;

            var firstHasText = first.Any(field => !IsNumeric(field));
            var nextHasData = rows.Skip(1).Take(6).Any(row => row.Any(field => !string.IsNullOrWhiteSpace(field)));
            return firstHasText && nextHasData;
        }

        private static bool IsNumeric(string value)
        {
            double number;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number);
        }

        private static List<List<string>> ParseRows(string text, char delimiter)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }

                    continue;
                }

                if (c == '"' && field.Length == 0)
                {
                    inQuotes = true;
                }
                else if (c == delimiter)
                {
                    row.Add(field.ToString());
                    field.Clear();
                }
                else if (c == '\r' || c == '\n')
                {
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                }
                else
                {
                    field.Append(c);
                }
            }

            if (field.Length > 0 || row.Count > 0 || text.EndsWith(delimiter.ToString(), StringComparison.Ordinal))
            {
                row.Add(field.ToString());
                rows.Add(row);
            }

            return rows;
        }

        private static string GetExcelColumnName(int index)
        {
            var name = "";
            index++;
            while (index > 0)
            {
                var modulo = (index - 1) % 26;
                name = Convert.ToChar('A' + modulo) + name;
                index = (index - modulo) / 26;
            }

            return name;
        }
    }
}
