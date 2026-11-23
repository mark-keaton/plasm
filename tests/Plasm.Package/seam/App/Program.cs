// What C# is given, switched over with no default arm: the compiler names a
// case a switch does not handle (seam/run.sh removes one to see it refused).
using System.Text.Json;
using System.Text.Json.Serialization;
using Plasm.CSharp;
using Seam.Data;

var notes = Notes.Open(args[0]);
var json = SeamJson.Default;

string Found(Found<NoteView> found) => found switch
{
    NoteView v => "found " + JsonSerializer.Serialize(v, json.NoteView),
    Missing m => "missing " + JsonSerializer.Serialize(m, json.Missing),
};

string Changed(Changed<NoteView> changed) => changed switch
{
    NoteView v => "changed " + JsonSerializer.Serialize(v, json.NoteView),
    Missing m => "missing " + JsonSerializer.Serialize(m, json.Missing),
    Refused r => "refused " + JsonSerializer.Serialize(r, json.Refused),
    Stale s => "stale " + JsonSerializer.Serialize(s, json.Stale),
    Invalid i => "invalid " + JsonSerializer.Serialize(i, json.Invalid),
};

string Added(Added<NoteView> added) => added switch
{
    NoteView v => "added " + JsonSerializer.Serialize(v, json.NoteView),
    Invalid i => "invalid " + JsonSerializer.Serialize(i, json.Invalid),
};

Console.WriteLine(Added(notes.Add("Groceries")));
Console.WriteLine(Added(notes.Add("Groceries")));
Console.WriteLine(Found(notes.Read(Guid.Parse("01900000-0000-7000-8000-000000000001"))));
Console.WriteLine(Found(notes.Read(Guid.Empty)));
Console.WriteLine(Changed(notes.RenameStale()));
Console.WriteLine(Changed(notes.RetitleFiledStale()));
var back = JsonSerializer.Deserialize("{\"kind\":\"withdrawn\",\"reason\":\"lamp\"}", json.ConditionView);
Console.WriteLine("read back: " + (back.Value is Withdrawn w ? "Withdrawn " + w.Reason : "not Withdrawn"));

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NoteView))]
[JsonSerializable(typeof(ConditionView))]
[JsonSerializable(typeof(Bookable))]
[JsonSerializable(typeof(Withdrawn))]
[JsonSerializable(typeof(Missing))]
[JsonSerializable(typeof(Refused))]
[JsonSerializable(typeof(Stale))]
[JsonSerializable(typeof(Invalid))]
partial class SeamJson : JsonSerializerContext;
