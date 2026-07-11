using Interpreter.LiveDiagnostics;
using Clever.Model.Utils;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Clev3r.AgentCore.Tests
{
    public class EditorRegressionTests
    {
        [Fact]
        public void IndentedElseIfDoesNotTreatKeywordSuffixAsVariable()
        {
            var path = Path.Combine(Path.GetTempPath(), "clev3r-indented-elseif.bp");
            var source = string.Join(Environment.NewLine, new[]
            {
                "leftPower = 0",
                "If leftPower > 100 Then",
                "    leftPower = 100",
                "    ElseIf leftPower < -100 Then",
                "    leftPower = -100",
                "EndIf"
            });
            var snapshot = new BpWorkspaceSnapshot
            {
                RootFilePath = path,
                LibraryRoot = Path.GetTempPath()
            };
            snapshot.Files.Add(new BpSourceFile(path, source, false));

            var result = BpLiveAnalyzer.Analyze(snapshot);

            Assert.DoesNotContain(result.Diagnostics, diagnostic =>
                string.Equals(diagnostic.Code, "BP3001", StringComparison.OrdinalIgnoreCase)
                || diagnostic.Message.IndexOf("seIf", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void CsvRowDeletionRaisesChangeAndIsSerialized()
        {
            var document = CsvTableDocument.Parse("Name,Value\nAlpha,1\nBeta,2");
            var changed = false;

            using (var tracker = new CsvTableChangeTracker(document.Table))
            {
                tracker.Changed += (sender, args) => changed = true;
                document.Table.Rows[0].Delete();
            }

            var csv = document.ToCsv();
            Assert.True(changed);
            Assert.DoesNotContain("Alpha,1", csv, StringComparison.Ordinal);
            Assert.Contains("Beta,2", csv, StringComparison.Ordinal);
        }

        [Fact]
        public void CsvLineChartGroupsColumnSelectionIntoSeries()
        {
            var cells = new[]
            {
                new CsvChartCell(0, 0, "Температура", "10"),
                new CsvChartCell(0, 1, "Давление", "20"),
                new CsvChartCell(1, 0, "Температура", "11.5"),
                new CsvChartCell(1, 1, "Давление", "21")
            };

            var data = CsvChartDataBuilder.Build(cells, CsvChartKind.Line, CsvChartLineOrientation.SeriesByColumn);

            Assert.Equal(2, data.Series.Count);
            Assert.Equal("Температура", data.Series[0].Name);
            Assert.Equal(new[] { 1d, 2d }, data.Series[0].Points.Select(point => point.X));
            Assert.Equal(new[] { 10d, 11.5d }, data.Series[0].Points.Select(point => point.Y));
            Assert.Equal("Строки", data.XAxisTitle);
        }

        [Fact]
        public void CsvLineChartGroupsRowSelectionAndUsesColumnLabels()
        {
            var cells = new[]
            {
                new CsvChartCell(3, 0, "A", "2"),
                new CsvChartCell(3, 1, "B", "3"),
                new CsvChartCell(4, 0, "A", "5"),
                new CsvChartCell(4, 1, "B", "8")
            };

            var data = CsvChartDataBuilder.Build(cells, CsvChartKind.Line, CsvChartLineOrientation.SeriesByRow);

            Assert.Equal(2, data.Series.Count);
            Assert.Equal("Строка 4", data.Series[0].Name);
            Assert.Equal(new[] { "A", "B" }, data.Series[0].Points.Select(point => point.Label));
            Assert.Equal("Столбцы", data.XAxisTitle);
        }

        [Fact]
        public void CsvChartsIgnoreInvalidValuesAndPieRequiresPositiveValues()
        {
            var cells = new[]
            {
                new CsvChartCell(0, 0, "A", "5"),
                new CsvChartCell(0, 1, "B", ""),
                new CsvChartCell(1, 0, "A", "не число"),
                new CsvChartCell(1, 1, "B", "-2"),
                new CsvChartCell(2, 0, "A", "0")
            };

            var distribution = CsvChartDataBuilder.Build(cells, CsvChartKind.Distribution, CsvChartLineOrientation.Auto);
            var pie = CsvChartDataBuilder.Build(cells, CsvChartKind.Pie, CsvChartLineOrientation.Auto);

            Assert.Equal(3, distribution.Series.SelectMany(series => series.Points).Count());
            Assert.Single(pie.Slices);
            Assert.Equal(5d, pie.Slices[0].Value);
        }
    }
}
