// Prints what the F# library describes, under the runtime and natively.
try
{
    foreach (var line in Desk.LendingDesk.Describe()) Console.WriteLine(line);
    return 0;
}
catch (Exception ex)
{
    var inner = ex;
    while (inner.InnerException is not null) inner = inner.InnerException;
    Console.WriteLine("refused: " + inner.Message);
    return 1;
}
