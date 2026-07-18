using System.Reflection;
using System.Text;

namespace CurlHttp.IntegrationTests.ApiCoverage;

/// <summary>
/// Reflection-based enumeration of the COMPLETE public request-relevant API
/// surface the application can use against this handler:
///  - every public declared member of <see cref="HttpClient"/> (methods,
///    properties, constructors excluded) plus the inherited
///    HttpMessageInvoker sends and Dispose,
///  - every public static extension on
///    <see cref="System.Net.Http.Json.HttpClientJsonExtensions"/>.
///
/// Signatures are canonicalized deterministically (short type names,
/// normalized generic parameters) so the committed baseline is stable for a
/// pinned SDK (see global.json).
/// </summary>
public static class HttpClientApiInventory
{
    public static IReadOnlyList<string> EnumerateCurrentSurface()
    {
        var signatures = new SortedSet<string>(StringComparer.Ordinal);

        foreach (MethodInfo method in typeof(HttpClient).GetMethods(
            BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.DeclaringType == typeof(object) || method.IsSpecialName)
            {
                continue; // property accessors handled below; object plumbing irrelevant
            }
            signatures.Add(Format("HttpClient", method));
        }

        foreach (PropertyInfo property in typeof(HttpClient).GetProperties(
            BindingFlags.Public | BindingFlags.Instance))
        {
            signatures.Add($"HttpClient.{property.Name} {{ {(property.CanWrite ? "get; set;" : "get;")} }}");
        }

        foreach (MethodInfo method in typeof(HttpClientJsonExtensions).GetMethods(
            BindingFlags.Public | BindingFlags.Static))
        {
            signatures.Add(Format("HttpClientJsonExtensions", method));
        }

        // Every public request-body content type the caller can hand to the
        // handler. Enumerated from the two framework assemblies (System.Net.Http
        // and System.Net.Http.Json) so a newly-shipped BCL content kind — or a
        // dropped test — trips the gate. GetExportedTypes() yields public types
        // only, so the internal JsonContent<T> is excluded while the public,
        // caller-constructible (via JsonContent.Create) abstract JsonContent is
        // included. A distinct "HttpContent:" prefix keeps these signatures out
        // of the method-signature namespace.
        foreach (Type type in new[]
                 {
                     typeof(HttpContent).Assembly,
                     typeof(System.Net.Http.Json.JsonContent).Assembly,
                 }
                 .SelectMany(a => a.GetExportedTypes())
                 .Where(t => t != typeof(HttpContent) && typeof(HttpContent).IsAssignableFrom(t)))
        {
            signatures.Add($"HttpContent:{type.Name}");
        }

        return [.. signatures];
    }

    public static string Format(string declaringName, MethodInfo method)
    {
        var sb = new StringBuilder();
        sb.Append(declaringName).Append('.').Append(method.Name);

        Dictionary<Type, string>? genericMap = null;
        if (method.IsGenericMethodDefinition)
        {
            Type[] args = method.GetGenericArguments();
            genericMap = new Dictionary<Type, string>();
            for (int i = 0; i < args.Length; i++)
            {
                genericMap[args[i]] = args.Length == 1 ? "T" : $"T{i + 1}";
            }
            sb.Append('<').Append(string.Join(", ", genericMap.Values)).Append('>');
        }

        sb.Append('(');
        sb.Append(string.Join(", ", method.GetParameters()
            .Select(p => FriendlyName(p.ParameterType, genericMap))));
        sb.Append(')');
        return sb.ToString();
    }

    private static string FriendlyName(Type type, Dictionary<Type, string>? genericMap)
    {
        if (genericMap is not null && genericMap.TryGetValue(type, out string? mapped))
        {
            return mapped;
        }
        if (type.IsGenericParameter)
        {
            return type.Name;
        }
        if (type.IsArray)
        {
            return FriendlyName(type.GetElementType()!, genericMap) + "[]";
        }
        if (!type.IsGenericType)
        {
            return type.Name;
        }
        string name = type.Name;
        int backtick = name.IndexOf('`');
        if (backtick >= 0)
        {
            name = name[..backtick];
        }
        return name + "<" +
               string.Join(", ", type.GetGenericArguments().Select(a => FriendlyName(a, genericMap))) +
               ">";
    }
}
