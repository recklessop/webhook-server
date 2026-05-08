using System.Text.Json.Nodes;

namespace WebhookServer.Core.Execution;

/// <summary>
/// Resolves {{path}} tokens against an <see cref="ExecutionContext"/>. Each whitespace-
/// separated token in the template becomes one argv entry.
/// Path grammar:
///   {{body.foo.bar}}      JSON path into the body
///   {{header.X-Foo}}      header by name (case-insensitive)
///   {{query.bar}}         query param
///   {{route.slug}}        route value
/// Missing paths render as empty string.
/// </summary>
public static class ArgTemplateRenderer
{
    public static List<string> Render(string? template, ExecutionContext ctx)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(template)) return args;

        foreach (var token in template.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            args.Add(RenderToken(token, ctx));
        }
        return args;
    }

    private static string RenderToken(string token, ExecutionContext ctx)
    {
        // Replace every {{...}} occurrence inside the token in a single left-to-right pass.
        var result = new System.Text.StringBuilder(token.Length);
        var i = 0;
        while (i < token.Length)
        {
            var open = token.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0)
            {
                result.Append(token, i, token.Length - i);
                break;
            }
            result.Append(token, i, open - i);
            var close = token.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                // Unclosed token — treat the rest as literal.
                result.Append(token, open, token.Length - open);
                break;
            }
            var path = token.Substring(open + 2, close - (open + 2)).Trim();
            result.Append(Resolve(path, ctx));
            i = close + 2;
        }
        return result.ToString();
    }

    private static string Resolve(string path, ExecutionContext ctx)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var dot = path.IndexOf('.');
        if (dot < 0) return "";

        var scope = path[..dot];
        var rest = path[(dot + 1)..];

        return scope.ToLowerInvariant() switch
        {
            "body" => ResolveJson(ctx.BodyJson, rest),
            "header" => LookupCaseInsensitive(ctx.Headers, rest),
            "query" => LookupCaseInsensitive(ctx.Query, rest),
            "route" => LookupCaseInsensitive(ctx.Route, rest),
            _ => "",
        };
    }

    private static string ResolveJson(JsonNode? root, string path)
    {
        if (root is null) return "";
        JsonNode? cursor = root;
        foreach (var segment in path.Split('.'))
        {
            if (cursor is null) return "";

            if (cursor is JsonObject obj)
            {
                cursor = obj.TryGetPropertyValue(segment, out var next) ? next : null;
                continue;
            }

            if (cursor is JsonArray arr && int.TryParse(segment, out var idx))
            {
                cursor = idx >= 0 && idx < arr.Count ? arr[idx] : null;
                continue;
            }

            return "";
        }

        return cursor switch
        {
            null => "",
            JsonValue v => v.ToString(),
            _ => cursor.ToJsonString(),
        };
    }

    private static string LookupCaseInsensitive(IReadOnlyDictionary<string, string> map, string key)
    {
        foreach (var kvp in map)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }
        return "";
    }
}
