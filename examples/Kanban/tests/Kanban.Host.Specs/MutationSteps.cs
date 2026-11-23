using Plasm.CSharp;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reqnroll;

namespace Kanban.Host.Specs;

/// features/host/mutations.feature
public sealed partial class Steps
{
    /// The schema as SDL: the host allows no introspection outside development.
    [When("I read the schema")]
    public Task Schema() => Send(HttpMethod.Get, "/graphql?sdl", null);

    /// Each field of the SDL's `type Mutation`, as `name: Type`.
    IEnumerable<string> MutationFields()
    {
        var lines = body.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("type Mutation "));
        if (start < 0) throw new Exception("no Mutation in the schema:\n" + body);
        return lines.Skip(start + 1).TakeWhile(l => l != "}")
            .Select(l => Regex.Match(l, @"^  (\w+)(?:\(.*\))?: (\S+)"))
            .Where(m => m.Success)
            .Select(m => $"{m.Groups[1].Value}: {m.Groups[2].Value}");
    }

    [Then("the mutations are:")]
    public void Mutations(DataTable expected) =>
        Equal(string.Join("\n  ", expected.Rows.Select(r => $"{r["mutation"]}: {r["answers"]}")),
            string.Join("\n  ", MutationFields().Order(StringComparer.Ordinal)), "the mutations");

    [Then("the union {string} is {string}")]
    public void Union(string name, string members)
    {
        var union = body.Split('\n').SingleOrDefault(l => l.StartsWith($"union {name} = ")) ?? throw new Exception($"no union {name} in the schema:\n{body}");
        Equal(members, union[$"union {name} = ".Length..].Trim(), $"the union {name}");
    }

    /// The one field the request asked for.
    JsonNode Answered()
    {
        var data = JsonNode.Parse(body)!["data"]?.AsObject();
        if (data is null || data.Count != 1) throw new Exception("not one field in the answer: " + body);
        return data.Single().Value ?? throw new Exception("null in the answer: " + body);
    }

    [Then(@"^the answer is an? ""([^""]*)""$")]
    public void Case(string expected) => Equal(expected, (string?)Answered()["__typename"], "the case answered in " + body);

    [Then(@"^the answer is a ""Refused"" with the code ""(.*)""$")]
    public void RefusedWith(string code)
    {
        Case("Refused");
        Equal(code, (string?)Answered()["code"], "the code in " + body);
    }

    [Then(@"^the answer is an ""Invalid"" on the field ""(.*)""$")]
    public void InvalidOn(string field)
    {
        Case("Invalid");
        if (!Answered()["problems"]!.AsArray().Any(p => (string)p!["field"]! == field)) throw new Exception($"no problem on \"{field}\" in {body}");
    }

    [Then("the answer is the GraphQL error {string}")]
    public void ErrorCode(string code)
    {
        var errors = JsonNode.Parse(body)!["errors"]?.AsArray() ?? throw new Exception("no error in " + body);
        Equal(code, (string?)errors[0]!["extensions"]?["code"], "the error's code in " + body);
    }
}
