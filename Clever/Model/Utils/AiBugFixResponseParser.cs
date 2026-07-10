using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Clever.Model.Utils
{
    /// <summary>
    /// Strict parser for the AI repair schema. Invalid provider output becomes a non-applicable
    /// response instead of being repaired, guessed, or interpreted as source code.
    /// </summary>
    internal static class AiBugFixResponseParser
    {
        private const int MaxDiagnostics = 32;
        private const int MaxEdits = 50;
        private const int MaxReasonLength = 1000;
        private const int MaxTextLength = 4096;
        private const int MaxRawResponseLength = 131072;

        internal static AiBugFixResponse Parse(string rawResponse)
        {
            var response = new AiBugFixResponse { RawResponse = rawResponse ?? "" };
            if (string.IsNullOrWhiteSpace(rawResponse))
            {
                response.ValidationError = "The AI provider returned an empty repair response.";
                return response;
            }
            if (rawResponse.Length > MaxRawResponseLength)
            {
                response.ValidationError = "The AI provider returned an oversized repair response.";
                return response;
            }

            try
            {
                using (var document = JsonDocument.Parse(rawResponse))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        return Invalid(response, "The AI repair response root must be a JSON object.");

                    JsonElement action;
                    JsonElement diagnosticIds;
                    JsonElement reason;
                    JsonElement edits;
                    string error;
                    if (!TryGetExactProperties(document.RootElement,
                        new[] { "action", "diagnosticIds", "reason", "edits" },
                        out action, out diagnosticIds, out reason, out edits, out error))
                        return Invalid(response, error);

                    if (action.ValueKind != JsonValueKind.String)
                        return Invalid(response, "The action field must be a string.");
                    response.Action = action.GetString();
                    if (!string.Equals(response.Action, "apply", StringComparison.Ordinal)
                        && !string.Equals(response.Action, "cannot_fix", StringComparison.Ordinal))
                        return Invalid(response, "The action field must be apply or cannot_fix.");

                    if (reason.ValueKind != JsonValueKind.String)
                        return Invalid(response, "The reason field must be a string.");
                    response.Reason = reason.GetString();
                    if (string.IsNullOrWhiteSpace(response.Reason) || response.Reason.Length > MaxReasonLength)
                        return Invalid(response, "The reason field is missing or too long.");

                    if (!TryReadDiagnosticIds(diagnosticIds, response.DiagnosticIds, out error))
                        return Invalid(response, error);
                    if (!TryReadEdits(edits, response.Edits, out error))
                        return Invalid(response, error);

                    if (string.Equals(response.Action, "apply", StringComparison.Ordinal) && response.Edits.Count == 0)
                        return Invalid(response, "An apply response requires at least one edit.");
                    if (string.Equals(response.Action, "cannot_fix", StringComparison.Ordinal) && response.Edits.Count != 0)
                        return Invalid(response, "A cannot_fix response must not contain edits.");

                    response.IsValid = true;
                    return response;
                }
            }
            catch (JsonException)
            {
                return Invalid(response, "The AI repair response is not valid JSON.");
            }
            catch (Exception ex)
            {
                return Invalid(response, "The AI repair response could not be read: " + ex.Message);
            }
        }

        private static bool TryReadDiagnosticIds(JsonElement value, IList<string> result, out string error)
        {
            error = null;
            if (value.ValueKind != JsonValueKind.Array)
            {
                error = "The diagnosticIds field must be an array.";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    error = "Every diagnostic id must be a string.";
                    return false;
                }

                var id = item.GetString();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || !seen.Add(id))
                {
                    error = "Diagnostic ids must be unique, non-empty and short.";
                    return false;
                }
                result.Add(id);
                if (result.Count > MaxDiagnostics)
                {
                    error = "Too many diagnostic ids were returned.";
                    return false;
                }
            }

            if (result.Count == 0)
            {
                error = "At least one diagnostic id is required.";
                return false;
            }
            return true;
        }

        private static bool TryReadEdits(JsonElement value, IList<AiBugFixEdit> result, out string error)
        {
            error = null;
            if (value.ValueKind != JsonValueKind.Array)
            {
                error = "The edits field must be an array.";
                return false;
            }

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    error = "Every repair edit must be an object.";
                    return false;
                }

                JsonElement filePath;
                JsonElement start;
                JsonElement length;
                JsonElement expectedText;
                JsonElement replacementText;
                string propertyError;
                if (!TryGetExactProperties(item,
                    new[] { "filePath", "start", "length", "expectedText", "replacementText" },
                    out filePath, out start, out length, out expectedText, out replacementText, out propertyError))
                {
                    error = propertyError;
                    return false;
                }

                if (filePath.ValueKind != JsonValueKind.String || start.ValueKind != JsonValueKind.Number
                    || length.ValueKind != JsonValueKind.Number || expectedText.ValueKind != JsonValueKind.String
                    || replacementText.ValueKind != JsonValueKind.String)
                {
                    error = "Repair edit fields have invalid JSON types.";
                    return false;
                }

                int startValue;
                int lengthValue;
                if (!start.TryGetInt32(out startValue) || !length.TryGetInt32(out lengthValue)
                    || startValue < 0 || lengthValue < 0)
                {
                    error = "Repair edit offsets must be non-negative 32-bit integers.";
                    return false;
                }

                var path = filePath.GetString();
                var expected = expectedText.GetString();
                var replacement = replacementText.GetString();
                if (string.IsNullOrWhiteSpace(path) || path.Length > 2048
                    || expected == null || replacement == null
                    || expected.Length > MaxTextLength || replacement.Length > MaxTextLength
                    || expected.Length != lengthValue)
                {
                    error = "Repair edit contents are missing, inconsistent or too large.";
                    return false;
                }

                result.Add(new AiBugFixEdit
                {
                    FilePath = path,
                    Start = startValue,
                    Length = lengthValue,
                    ExpectedText = expected,
                    ReplacementText = replacement
                });
                if (result.Count > MaxEdits)
                {
                    error = "Too many repair edits were returned.";
                    return false;
                }
            }
            return true;
        }

        private static bool TryGetExactProperties(JsonElement source, string[] expectedNames,
            out JsonElement first, out JsonElement second, out JsonElement third, out JsonElement fourth, out string error)
        {
            first = default(JsonElement);
            second = default(JsonElement);
            third = default(JsonElement);
            fourth = default(JsonElement);
            error = null;

            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in source.EnumerateObject())
            {
                if (Array.IndexOf(expectedNames, property.Name) < 0 || values.ContainsKey(property.Name))
                {
                    error = "The AI repair response contains an unknown or duplicate JSON property.";
                    return false;
                }
                values.Add(property.Name, property.Value);
            }

            if (values.Count != expectedNames.Length)
            {
                error = "The AI repair response is missing a required JSON property.";
                return false;
            }

            first = values[expectedNames[0]];
            second = values[expectedNames[1]];
            third = values[expectedNames[2]];
            fourth = values[expectedNames[3]];
            return true;
        }

        private static bool TryGetExactProperties(JsonElement source, string[] expectedNames,
            out JsonElement first, out JsonElement second, out JsonElement third, out JsonElement fourth,
            out JsonElement fifth, out string error)
        {
            first = default(JsonElement);
            second = default(JsonElement);
            third = default(JsonElement);
            fourth = default(JsonElement);
            fifth = default(JsonElement);
            error = null;

            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in source.EnumerateObject())
            {
                if (Array.IndexOf(expectedNames, property.Name) < 0 || values.ContainsKey(property.Name))
                {
                    error = "The AI repair response contains an unknown or duplicate JSON property.";
                    return false;
                }
                values.Add(property.Name, property.Value);
            }

            if (values.Count != expectedNames.Length)
            {
                error = "The AI repair response is missing a required JSON property.";
                return false;
            }

            first = values[expectedNames[0]];
            second = values[expectedNames[1]];
            third = values[expectedNames[2]];
            fourth = values[expectedNames[3]];
            fifth = values[expectedNames[4]];
            return true;
        }

        private static AiBugFixResponse Invalid(AiBugFixResponse response, string error)
        {
            response.IsValid = false;
            response.ValidationError = error;
            response.DiagnosticIds.Clear();
            response.Edits.Clear();
            return response;
        }
    }
}
