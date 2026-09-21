using System.Reflection;
using System.Text;
using Hermes.Messaging.Infrastructure;

namespace Hermes.Messaging.Tests.Infrastructure;

/// <summary>
/// Public API surface guard (0.4.0-alpha). Snapshots every exported (public) type and member of the
/// Hermes.Messaging assembly and compares it to a checked-in baseline (docs/api/PublicAPI.txt).
/// <para>
/// If this test fails, a public type/member was added, removed, or changed. That is either:
/// (a) an accidental leak of an implementation detail — internalize it; or
/// (b) a deliberate public-API change — regenerate the baseline by setting the environment variable
/// <c>HERMES_UPDATE_PUBLIC_API=1</c> and re-running, then review the diff before committing.
/// </para>
/// This is the machine-readable guard that prevents accidental public surface growth before the
/// 0.5.0-beta API freeze.
/// </summary>
public sealed class PublicApiSurfaceTests
{
    [Fact]
    public void PublicApiSurface_MatchesBaseline()
    {
        var actual = BuildSurface();
        var baselinePath = LocateBaseline();

        if (Environment.GetEnvironmentVariable("HERMES_UPDATE_PUBLIC_API") == "1")
        {
            File.WriteAllText(baselinePath, actual);
        }

        Assert.True(File.Exists(baselinePath),
            $"Public API baseline not found at {baselinePath}. Run once with HERMES_UPDATE_PUBLIC_API=1 to create it.");

        var expected = Normalize(File.ReadAllText(baselinePath));
        var actualNormalized = Normalize(actual);

        Assert.True(expected == actualNormalized, BuildDiffMessage(expected, actualNormalized, baselinePath));
    }

    private static string BuildSurface()
    {
        var assembly = typeof(IMessageBus).Assembly;
        var lines = new List<string>();

        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            lines.Add($"TYPE {DescribeType(type)}");

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly;

            var members = type.GetMembers(flags)
                .Where(IsVisibleMember)
                .Select(DescribeMember)
                .Where(s => s is not null)
                .Select(s => s!)
                .OrderBy(s => s, StringComparer.Ordinal);

            foreach (var member in members)
            {
                lines.Add($"  {member}");
            }
        }

        return string.Join("\n", lines) + "\n";
    }

    private static bool IsVisibleMember(MemberInfo m) => m switch
    {
        MethodInfo method => method.IsPublic,
        ConstructorInfo ctor => ctor.IsPublic,
        FieldInfo field => field.IsPublic,
        PropertyInfo => true,
        EventInfo => true,
        Type nested => nested.IsNestedPublic,
        _ => false
    };

    private static string DescribeType(Type type)
    {
        var kind = type.IsEnum ? "enum"
            : type.IsInterface ? "interface"
            : type.IsValueType ? "struct"
            : "class";
        var name = FormatTypeName(type);
        var bases = new List<string>();
        if (type.BaseType is { } bt && bt != typeof(object) && bt != typeof(ValueType) && !type.IsEnum)
        {
            bases.Add(FormatTypeName(bt));
        }
        bases.AddRange(type.GetInterfaces()
            .Where(i => i.IsPublic || i.IsNestedPublic)
            .Select(FormatTypeName)
            .OrderBy(s => s, StringComparer.Ordinal));
        var baseSuffix = bases.Count > 0 ? " : " + string.Join(", ", bases) : string.Empty;
        return $"{kind} {name}{baseSuffix}";
    }

    private static string? DescribeMember(MemberInfo member)
    {
        switch (member)
        {
            case ConstructorInfo ctor:
                return $".ctor({FormatParameters(ctor.GetParameters())})";
            case PropertyInfo prop:
                var acc = new List<string>();
                if (prop.GetMethod is { IsPublic: true }) acc.Add("get");
                if (prop.SetMethod is { IsPublic: true }) acc.Add(prop.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit") ? "init" : "set");
                if (acc.Count == 0) return null;
                return $"{FormatTypeName(prop.PropertyType)} {prop.Name} {{ {string.Join("; ", acc)} }}";
            case FieldInfo field:
                var constPrefix = field.IsLiteral ? "const " : field.IsInitOnly ? "readonly " : string.Empty;
                var staticPrefix = field.IsStatic && !field.IsLiteral ? "static " : string.Empty;
                return $"{staticPrefix}{constPrefix}{FormatTypeName(field.FieldType)} {field.Name}";
            case EventInfo evt:
                return $"event {FormatTypeName(evt.EventHandlerType!)} {evt.Name}";
            case MethodInfo method:
                if (method.IsSpecialName) return null; // property/event accessors, operators handled elsewhere
                if (!method.IsPublic) return null;
                var staticMod = method.IsStatic ? "static " : string.Empty;
                var generics = method.IsGenericMethodDefinition
                    ? "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">"
                    : string.Empty;
                return $"{staticMod}{FormatTypeName(method.ReturnType)} {method.Name}{generics}({FormatParameters(method.GetParameters())})";
            default:
                return null;
        }
    }

    private static string FormatParameters(ParameterInfo[] parameters)
        => string.Join(", ", parameters.Select(p =>
        {
            var prefix = p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : string.Empty;
            return $"{prefix}{FormatTypeName(p.ParameterType)} {p.Name}";
        }));

    private static string FormatTypeName(Type type)
    {
        if (type.IsByRef) type = type.GetElementType()!;

        if (type.IsGenericParameter) return type.Name;

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return FormatTypeName(underlying) + "?";
        }

        if (type.IsArray)
        {
            return FormatTypeName(type.GetElementType()!) + "[]";
        }

        if (!type.IsGenericType)
        {
            return Aliases.TryGetValue(type.FullName ?? type.Name, out var alias) ? alias : (type.FullName ?? type.Name);
        }

        var def = type.GetGenericTypeDefinition();
        var baseName = (def.FullName ?? def.Name);
        var tick = baseName.IndexOf('`');
        if (tick >= 0) baseName = baseName[..tick];
        var args = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
        return $"{baseName}<{args}>";
    }

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["System.Void"] = "void",
        ["System.Boolean"] = "bool",
        ["System.Int32"] = "int",
        ["System.Int64"] = "long",
        ["System.String"] = "string",
        ["System.Object"] = "object",
        ["System.Guid"] = "System.Guid"
    };

    private static string Normalize(string s)
        => s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n') + "\n";

    private static string LocateBaseline()
    {
        // Walk up from the test assembly location to the repo root (contains Hermes.Messaging.slnx).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hermes.Messaging.slnx")))
        {
            dir = dir.Parent;
        }
        var root = dir?.FullName ?? AppContext.BaseDirectory;
        return Path.Combine(root, "docs", "api", "PublicAPI.txt");
    }

    private static string BuildDiffMessage(string expected, string actual, string baselinePath)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var expectedSet = new HashSet<string>(expectedLines);
        var actualSet = new HashSet<string>(actualLines);

        var added = actualLines.Where(l => l.Length > 0 && !expectedSet.Contains(l)).ToList();
        var removed = expectedLines.Where(l => l.Length > 0 && !actualSet.Contains(l)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Public API surface changed vs baseline (docs/api/PublicAPI.txt).");
        sb.AppendLine("If deliberate, regenerate with HERMES_UPDATE_PUBLIC_API=1 and review the diff.");
        if (added.Count > 0)
        {
            sb.AppendLine($"\n+ ADDED (accidental public surface? internalize, or record deliberately):");
            foreach (var l in added) sb.AppendLine($"  + {l}");
        }
        if (removed.Count > 0)
        {
            sb.AppendLine($"\n- REMOVED (breaking change — must be intentional):");
            foreach (var l in removed) sb.AppendLine($"  - {l}");
        }
        sb.AppendLine($"\nBaseline: {baselinePath}");
        return sb.ToString();
    }
}
