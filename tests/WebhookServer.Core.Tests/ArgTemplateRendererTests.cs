using System.Text;
using System.Text.Json.Nodes;
using WebhookServer.Core.Execution;
using ExecCtx = WebhookServer.Core.Execution.ExecutionContext;
using Xunit;

namespace WebhookServer.Core.Tests;

public class ArgTemplateRendererTests
{
    private static ExecCtx Ctx(string body, Dictionary<string, string>? headers = null, Dictionary<string, string>? query = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return new ExecCtx
        {
            RunId = "r",
            Slug = "s",
            BodyBytes = bytes,
            BodyString = body,
            BodyJson = body.Length == 0 ? null : JsonNode.Parse(body),
            Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Query = query ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Route = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "slug", "deploy" } },
        };
    }

    [Fact]
    public void Whitespace_separated_tokens_become_separate_args()
    {
        var ctx = Ctx("{\"name\":\"alice\",\"id\":7}");
        var args = ArgTemplateRenderer.Render("{{body.name}} {{body.id}}", ctx);
        Assert.Equal(new[] { "alice", "7" }, args);
    }

    [Fact]
    public void Header_lookup_is_case_insensitive()
    {
        var ctx = Ctx("", headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-GitHub-Event"] = "push" });
        var args = ArgTemplateRenderer.Render("{{header.x-github-event}}", ctx);
        Assert.Equal(new[] { "push" }, args);
    }

    [Fact]
    public void Missing_path_renders_empty_string()
    {
        var ctx = Ctx("{}");
        var args = ArgTemplateRenderer.Render("{{body.nope}}", ctx);
        Assert.Equal(new[] { "" }, args);
    }

    [Fact]
    public void Route_value_resolves()
    {
        var ctx = Ctx("");
        var args = ArgTemplateRenderer.Render("{{route.slug}}", ctx);
        Assert.Equal(new[] { "deploy" }, args);
    }

    [Fact]
    public void Multiple_substitutions_in_one_token_are_concatenated()
    {
        var ctx = Ctx("{\"a\":\"x\",\"b\":\"y\"}");
        var args = ArgTemplateRenderer.Render("{{body.a}}-{{body.b}}", ctx);
        Assert.Equal(new[] { "x-y" }, args);
    }

    [Fact]
    public void Nested_json_path_resolves()
    {
        var ctx = Ctx("{\"repo\":{\"name\":\"acme\"}}");
        var args = ArgTemplateRenderer.Render("{{body.repo.name}}", ctx);
        Assert.Equal(new[] { "acme" }, args);
    }
}
