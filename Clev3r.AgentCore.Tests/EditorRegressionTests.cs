using Interpreter.LiveDiagnostics;
using Clever.Model.Utils;
using System;
using System.IO;
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
    }
}
