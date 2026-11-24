// Calls the C# class the seam wrote over the F# Orders, and prints each
// answer as the seam's source-generated JSON.
using System.Text.Json;
using Plasm.CSharp;
using Shop.Api;

var orders = new Orders(new Shop.Orders());
var ada = Guid.Parse("01900000-0000-7000-8000-000000000001");

Console.WriteLine("found " + await orders.OrderAsync(ada, default) switch
{
    OrderView o => JsonSerializer.Serialize(o, SeamJson.Default.OrderView),
    Missing m => "missing " + m.What,
});
Console.WriteLine("missing " + await orders.OrderAsync(Guid.Empty, default) switch
{
    OrderView o => o.Customer,
    Missing m => JsonSerializer.Serialize(m, SeamJson.Default.Missing),
});
Console.WriteLine("all " + JsonSerializer.Serialize(await orders.OrdersAsync(default), SeamJson.Default.OrderViewArray));
Console.WriteLine("added " + await orders.AddOrderAsync("Edsger", null, default) switch
{
    OrderView o => JsonSerializer.Serialize(o, SeamJson.Default.OrderView),
    Invalid i => "invalid",
});
Console.WriteLine("invalid " + await orders.AddOrderAsync(" ", "x", default) switch
{
    OrderView o => o.Customer,
    Invalid i => JsonSerializer.Serialize(i, SeamJson.Default.Invalid),
});
var back = JsonSerializer.Deserialize("""{"kind":"cancelled","reason":"lost"}""", SeamJson.Default.StatusView);
Console.WriteLine("read back: " + back switch
{
    Open => "open",
    Shipped s => "shipped " + s.At,
    Cancelled c => "cancelled " + c.Reason,
});
