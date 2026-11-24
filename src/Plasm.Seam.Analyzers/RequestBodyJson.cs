// A request body no JSON context lists: with a source-generated JSON context
// (as a native binary needs, and as the seam writes), ASP.NET reads a body
// only of a type a context lists. One left out builds, starts, and then
// answers every request to its endpoint with a 500 ("JsonTypeInfo metadata
// for type ... was not provided"). This stops the build instead, naming the
// type and where to list it.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Plasm.Seam.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RequestBodyJson : DiagnosticAnalyzer
{
    public static readonly DiagnosticDescriptor Unlisted = new(
        id: "SEAM030",
        title: "A request body no JSON context lists",
        messageFormat: "{0} is the body of {1}, and no JSON context lists it, so every such request would answer 500: list it in Seam.cs, [assembly: SeamJson(typeof({0}))], or in the host's own context with [JsonSerializable(typeof({0}))]",
        category: "Plasm.Seam",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "ASP.NET reads a request body through the application's JSON contexts; a type none lists fails each request at run time.",
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Unlisted];

    /// The Map methods whose handler's record parameters ASP.NET reads from the body.
    static readonly ImmutableHashSet<string> BodyMethods = ["MapPost", "MapPut", "MapPatch", "MapMethods", "Map"];

    /// Parameter types ASP.NET binds from the request itself, never from the body.
    static readonly ImmutableHashSet<string> Special =
    [
        "System.Threading.CancellationToken", "Microsoft.AspNetCore.Http.HttpContext", "Microsoft.AspNetCore.Http.HttpRequest",
        "Microsoft.AspNetCore.Http.HttpResponse", "System.Security.Claims.ClaimsPrincipal", "Microsoft.AspNetCore.Http.IFormFile",
        "Microsoft.AspNetCore.Http.IFormFileCollection", "Microsoft.AspNetCore.Http.IFormCollection", "System.IO.Stream",
        "System.IO.Pipelines.PipeReader",
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var jsonContext = start.Compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonSerializerContext");
            var serializable = start.Compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonSerializableAttribute");
            var routes = start.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions");
            if (jsonContext is null || serializable is null || routes is null) return;

            var listed = new ConcurrentDictionary<ITypeSymbol, bool>(SymbolEqualityComparer.Default);
            var contexts = 0;
            var bodies = new ConcurrentBag<(ITypeSymbol Type, Location Where, string Endpoint)>();

            start.RegisterSymbolAction(symbol =>
            {
                var type = (INamedTypeSymbol)symbol.Symbol;
                for (var b = type.BaseType; b is not null; b = b.BaseType)
                {
                    if (!SymbolEqualityComparer.Default.Equals(b, jsonContext)) continue;
                    System.Threading.Interlocked.Increment(ref contexts);
                    foreach (var a in type.GetAttributes())
                        if (SymbolEqualityComparer.Default.Equals(a.AttributeClass, serializable)
                            && a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is ITypeSymbol t)
                            listed[t] = true;
                    break;
                }
            }, SymbolKind.NamedType);

            start.RegisterOperationAction(op =>
            {
                var call = (IInvocationOperation)op.Operation;
                var method = call.TargetMethod;
                if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, routes)) return;
                var inferred = BodyMethods.Contains(method.Name);
                foreach (var argument in call.Arguments)
                {
                    if (argument.Parameter?.Type.ToDisplayString() != "System.Delegate") continue;
                    var handler = Handler(argument.Value);
                    if (handler is null) continue;
                    var endpoint = Endpoint(call, method.Name);
                    foreach (var p in handler.Parameters)
                        if (IsBody(p, inferred))
                            bodies.Add((p.Type, p.Locations.FirstOrDefault() ?? argument.Syntax.GetLocation(), endpoint));
                }
            }, OperationKind.Invocation);

            start.RegisterCompilationEndAction(end =>
            {
                // Without a source-generated context, ASP.NET reads JSON by reflection, and needs no list.
                if (contexts == 0) return;
                foreach (var (type, where, endpoint) in bodies)
                    if (!listed.ContainsKey(type))
                        end.ReportDiagnostic(Diagnostic.Create(Unlisted, where, type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), endpoint));
            });
        });
    }

    static IMethodSymbol? Handler(IOperation value)
    {
        while (value is IConversionOperation conversion) value = conversion.Operand;
        if (value is IDelegateCreationOperation creation) value = creation.Target;
        return value switch
        {
            IAnonymousFunctionOperation lambda => lambda.Symbol,
            IMethodReferenceOperation reference => reference.Method,
            _ => null,
        };
    }

    /// "POST /orders", as the call says it.
    static string Endpoint(IInvocationOperation call, string method)
    {
        var verb = method.StartsWith("Map") && method.Length > 3 ? method.Substring(3).ToUpperInvariant() : method;
        var pattern = call.Arguments.FirstOrDefault(a => a.Parameter?.Name == "pattern")?.Value.ConstantValue;
        return pattern is { HasValue: true, Value: string p } ? $"{verb} {p}" : $"this {verb}";
    }

    static bool IsBody(IParameterSymbol p, bool inferred)
    {
        foreach (var a in p.GetAttributes())
        {
            var name = a.AttributeClass?.Name ?? "";
            if (name == "FromBodyAttribute") return true;
            if (name.StartsWith("From") || name == "AsParametersAttribute") return false;
        }
        if (!inferred) return false;
        var type = p.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n ? n.TypeArguments[0] : p.Type;
        if (Special.Contains(type.ToDisplayString())) return false;
        // A record is what a request body is; a service, which ASP.NET also
        // takes as a parameter, is a class or an interface.
        return type.IsRecord;
    }
}
