using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Clev3r.AgentCore
{
    /// <summary>
    /// Produces app-server compatible dynamicTools descriptors without owning a transport.
    /// Current app-server versions support namespace descriptors; older 0.115-style schemas
    /// can consume the flat compatibility form.
    /// </summary>
    public static class DynamicToolWireAdapter
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public static string CreateDefinitionsJson(IEnumerable<AgentToolDefinition> definitions, bool supportsNamespaces)
        {
            var tools = (definitions ?? Enumerable.Empty<AgentToolDefinition>())
                .Where(item => item != null)
                .OrderBy(item => item.QualifiedName, StringComparer.Ordinal)
                .ToList();

            if (!supportsNamespaces)
            {
                var flat = tools.Select(item => new Dictionary<string, object>
                {
                    { "name", FlattenName(item) },
                    { "description", item.Description ?? "" },
                    { "inputSchema", ParseSchema(item.InputSchemaJson) },
                    { "deferLoading", item.DeferLoading }
                }).ToList();
                return JsonSerializer.Serialize(flat, Options);
            }

            var groups = tools.GroupBy(item => item.Namespace ?? "", StringComparer.Ordinal);
            var descriptors = new List<object>();
            foreach (var group in groups)
            {
                if (string.IsNullOrWhiteSpace(group.Key))
                {
                    descriptors.AddRange(group.Select(item => (object)new Dictionary<string, object>
                    {
                        { "type", "function" },
                        { "name", item.Name },
                        { "description", item.Description ?? "" },
                        { "inputSchema", ParseSchema(item.InputSchemaJson) },
                        { "deferLoading", item.DeferLoading }
                    }));
                    continue;
                }

                descriptors.Add(new Dictionary<string, object>
                {
                    { "type", "namespace" },
                    { "name", group.Key },
                    { "description", "CLEV3R IDE, Basic Plus documentation and EV3 robot tools." },
                    { "tools", group.Select(item => new Dictionary<string, object>
                        {
                            { "type", "function" },
                            { "name", item.Name },
                            { "description", item.Description ?? "" },
                            { "inputSchema", ParseSchema(item.InputSchemaJson) },
                            { "deferLoading", item.DeferLoading }
                        }).ToList()
                    }
                });
            }
            return JsonSerializer.Serialize(descriptors, Options);
        }

        public static string CreateToolCallResponseJson(AgentToolResult result)
        {
            result = result ?? AgentToolResult.Fail("tool_result_missing", "CLEV3R returned no tool result.");
            var envelope = JsonSerializer.Serialize(result, Options);
            var response = new Dictionary<string, object>
            {
                { "contentItems", new[] { new Dictionary<string, object>
                    {
                        { "type", "inputText" },
                        { "text", envelope }
                    }
                } },
                { "success", result.Success }
            };
            return JsonSerializer.Serialize(response, Options);
        }

        public static string ResolveQualifiedName(string namespaceName, string wireToolName, bool supportsNamespaces)
        {
            if (supportsNamespaces)
                return string.IsNullOrWhiteSpace(namespaceName) ? wireToolName : namespaceName + "." + wireToolName;
            if (!string.IsNullOrWhiteSpace(wireToolName) && wireToolName.StartsWith("clev3r_", StringComparison.OrdinalIgnoreCase))
                return "clev3r." + wireToolName.Substring("clev3r_".Length);
            return wireToolName;
        }

        private static string FlattenName(AgentToolDefinition definition)
        {
            return string.IsNullOrWhiteSpace(definition.Namespace)
                ? definition.Name
                : definition.Namespace + "_" + definition.Name;
        }

        private static JsonElement ParseSchema(string schema)
        {
            using (var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(schema) ? "{}" : schema))
                return document.RootElement.Clone();
        }
    }
}
