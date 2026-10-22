using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace NEW_CLEVER.AgentCore.Tests
{
    public sealed class BpExamplesCompileTests
    {
        [Theory]
        [InlineData("BreakAndContinue/BreakAndContinueTest.bp")]
        [InlineData("Functions/RGB_Test.bp")]
        [InlineData("Sensor1-4/Sensor1.bp")]
        public void TrackedBasicPlusExamplesCompile(string relativePath)
        {
            var root = FindRepositoryRoot();
            var runtime = Path.Combine(root, "NEW_CLEVER", "RuntimeData") + Path.DirectorySeparatorChar;
            var example = Path.Combine(runtime, "Examples", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(example), "Missing tracked example: " + example);

            var errors = new List<string>();
            var builder = new Interpreter.Builder("en");
            using (var interpreted = new MemoryStream())
            using (var compiled = new MemoryStream())
            using (var assembled = new MemoryStream())
            {
                builder.StartInterpreter(
                    File.ReadAllLines(example),
                    Path.GetDirectoryName(example) + Path.DirectorySeparatorChar,
                    Path.GetFileName(example),
                    runtime,
                    ".bp",
                    ".bpi",
                    ".bpm",
                    "~",
                    "en",
                    errors,
                    interpreted);
                Assert.True(errors.Count == 0, "Preprocessor: " + string.Join(Environment.NewLine, errors));

                errors.Clear();
                builder.StartCompiler(interpreted, compiled, errors);
                Assert.True(errors.Count == 0, "Compiler: " + string.Join(Environment.NewLine, errors));

                errors.Clear();
                builder.StartAssembler(compiled, assembled, errors);
                Assert.True(errors.Count == 0, "Assembler: " + string.Join(Environment.NewLine, errors));
                Assert.True(assembled.Length > 0, "Assembler produced an empty RBF image.");
            }
        }

        [Fact]
        public void ParserReportsInvalidExpressionInsteadOfThrowing()
        {
            var root = FindRepositoryRoot();
            var runtime = Path.Combine(root, "NEW_CLEVER", "RuntimeData") + Path.DirectorySeparatorChar;
            var errors = new List<string>();
            var builder = new Interpreter.Builder("en");

            using (var interpreted = new MemoryStream())
            {
                var preprocessing = Record.Exception(() => builder.StartInterpreter(
                    new[] { "value = \"text\" - 1" }, runtime, "invalid.bp", runtime,
                    ".bp", ".bpi", ".bpm", "~", "en", errors, interpreted));
                Assert.Null(preprocessing);
                Assert.NotEmpty(errors);
                Assert.Contains(errors, error => error.IndexOf("file:", StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "NEW_CLEVER.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate NEW_CLEVER.sln from the test output directory.");
        }
    }
}
