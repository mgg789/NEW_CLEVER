using NEW_CLEVER.Model.Agent;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NEW_CLEVER.AgentCore.Tests
{
    public sealed class CodexJsonRpcTransportTests
    {
        [Fact]
        public async Task CorrelatesOutOfOrderResponses()
        {
            var incoming = Utf8("{\"id\":2,\"result\":{\"value\":\"second\"}}\n" +
                                "{\"id\":1,\"result\":{\"value\":\"first\"}}\n");
            var outgoing = new MemoryStream();
            using (var transport = new CodexJsonRpcTransport(incoming, outgoing))
            {
                var first = transport.SendRequestAsync("first", new { }, CancellationToken.None);
                var second = transport.SendRequestAsync("second", new { }, CancellationToken.None);
                transport.Start();

                Assert.Equal("first", (await first).GetProperty("value").GetString());
                Assert.Equal("second", (await second).GetProperty("value").GetString());
            }
        }

        [Fact]
        public async Task DispatchesServerRequestAndWritesResponse()
        {
            var incoming = Utf8("{\"id\":\"tool-1\",\"method\":\"item/tool/call\",\"params\":{\"tool\":\"status\"}}\n");
            var outgoing = new MemoryStream();
            using (var transport = new CodexJsonRpcTransport(incoming, outgoing))
            {
                var handled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                transport.ServerRequestReceived += request =>
                {
                    handled.TrySetResult(true);
                    return Task.FromResult(CodexRpcServerResponse.Ok(new Dictionary<string, object> { { "success", true } }));
                };
                transport.Start();
                await handled.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await WaitUntilAsync(() => outgoing.Length > 0);
            }

            outgoing.Position = 0;
            using (var reader = new StreamReader(outgoing, Encoding.UTF8, false, 1024, true))
            using (var document = JsonDocument.Parse((await reader.ReadLineAsync())))
            {
                Assert.Equal("tool-1", document.RootElement.GetProperty("id").GetString());
                Assert.True(document.RootElement.GetProperty("result").GetProperty("success").GetBoolean());
            }
        }

        [Fact]
        public async Task MalformedInputFaultsTransport()
        {
            var incoming = Utf8("not-json\n");
            using (var outgoing = new MemoryStream())
            using (var transport = new CodexJsonRpcTransport(incoming, outgoing))
            {
                var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
                transport.Faulted += exception => fault.TrySetResult(exception);
                transport.Start();
                Assert.IsAssignableFrom<JsonException>(await fault.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            }
        }

        [Fact]
        public async Task RejectsOversizedOutgoingMessage()
        {
            using (var incoming = new MemoryStream())
            using (var outgoing = new MemoryStream())
            using (var transport = new CodexJsonRpcTransport(incoming, outgoing))
            {
                var huge = new string('x', CodexJsonRpcTransport.MaximumMessageBytes + 1);
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    transport.SendRequestAsync("large", new { value = huge }, CancellationToken.None));
            }
        }

        private static MemoryStream Utf8(string value)
        {
            return new MemoryStream(new UTF8Encoding(false).GetBytes(value), false);
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException();
                await Task.Delay(10);
            }
        }
    }
}
