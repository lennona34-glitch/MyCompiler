using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MyCompiler.Services;

/// <summary>
/// Post-processes an ilspycmd "-p" project so it builds with the current SDK:
///  • fixes the SDK / TargetFramework (netcoreappX.Y → netX.Y[-windows]),
///  • replaces leaked compiler-generated helper types (collection expressions, inline arrays)
///    with compilable equivalents and supplies the missing &lt;PrivateImplementationDetails&gt; helpers,
///  • strips compiler-reserved attributes (RefSafetyRules, NullablePublicOnly, …),
///  • for WPF apps, restores the original "{Assembly}.g.resources" blob so the decompiled
///    InitializeComponent() still finds its BAML at runtime.
/// </summary>
public static partial class DecompiledProjectRepair
{
    public static void Repair(string projectDir, string assemblyPath, Action<string> log)
    {
        var csproj = Directory.EnumerateFiles(projectDir, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (csproj is null) return;

        var fixes = new List<string>();
        try { FixSources(projectDir, fixes); } catch (Exception ex) { fixes.Add($"source repair skipped ({ex.Message})"); }
        try { FixProject(csproj, assemblyPath, fixes); } catch (Exception ex) { fixes.Add($"project repair skipped ({ex.Message})"); }

        if (fixes.Count > 0)
        {
            log($"  Repaired {Path.GetFileName(csproj)}:");
            foreach (var fix in fixes) log($"    • {fix}");
        }
    }

    // ───────────────────────── .csproj ─────────────────────────

    private static void FixProject(string csproj, string assemblyPath, List<string> fixes)
    {
        var doc = XDocument.Load(csproj, LoadOptions.PreserveWhitespace);
        var root = doc.Root!;

        if (root.Attribute("Sdk")?.Value is "Microsoft.NET.Sdk.WindowsDesktop")
        {
            root.SetAttributeValue("Sdk", "Microsoft.NET.Sdk");
            fixes.Add("SDK Microsoft.NET.Sdk.WindowsDesktop → Microsoft.NET.Sdk");
        }

        bool IsTrue(string name) => root.Descendants(name).Any(e => e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
        var isDesktop = IsTrue("UseWPF") || IsTrue("UseWindowsForms");
        var isWpf = IsTrue("UseWPF");

        foreach (var tfm in root.Descendants("TargetFramework"))
        {
            var old = tfm.Value.Trim();
            var value = NetCoreAppRegex().Replace(old, "net$1");
            if (isDesktop && Regex.IsMatch(value, @"^net([5-9]|\d{2,})\.\d+$")) value += "-windows";
            if (value != old)
            {
                tfm.Value = value;
                fixes.Add($"TargetFramework {old} → {value}");
            }
        }

        var firstGroup = root.Elements("PropertyGroup").FirstOrDefault();
        if (firstGroup is not null && !root.Descendants("Nullable").Any())
            firstGroup.Add(new XElement("Nullable", "annotations"));

        // ilspycmd pins LangVersion to what it detected, but its output can need newer features (e.g. ref locals in async).
        foreach (var lang in root.Descendants("LangVersion").Where(e => e.Value.Trim() is not ("latest" or "preview")))
        {
            fixes.Add($"LangVersion {lang.Value.Trim()} → latest");
            lang.Value = "latest";
        }

        if (isWpf && File.Exists(assemblyPath))
            FixWpfResources(root, Path.GetDirectoryName(csproj)!, assemblyPath, fixes);

        doc.Save(csproj);
    }

    private static void FixWpfResources(XElement root, string projectDir, string assemblyPath, List<string> fixes)
    {
        var (manifestNames, gResources) = ReadManifestResources(assemblyPath);
        if (gResources is null) return;

        var (name, bytes) = gResources.Value;
        File.WriteAllBytes(Path.Combine(projectDir, name), bytes);

        // ilspycmd explodes {Assembly}.g.resources into loose EmbeddedResource items (mainwindow.baml, images…).
        // Those aren't real manifest resources, so drop them and embed the original blob instead.
        var exploded = root.Descendants("EmbeddedResource")
            .Where(e => e.Attribute("Include") is not null)
            .Where(e => !manifestNames.Contains(e.Attribute("LogicalName")?.Value ?? e.Attribute("Include")!.Value))
            .ToList();
        foreach (var item in exploded) item.Remove();

        root.Add(new XElement("ItemGroup",
            new XElement("EmbeddedResource", new XAttribute("Include", name), new XAttribute("LogicalName", name))));
        fixes.Add($"restored WPF resources ({name}, replaces {exploded.Count} loose BAML/asset items)");
    }

    private static (HashSet<string> Names, (string Name, byte[] Bytes)? GResources) ReadManifestResources(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        (string, byte[])? gResources = null;

        var resourcesRva = pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress;
        foreach (var handle in md.ManifestResources)
        {
            var resource = md.GetManifestResource(handle);
            var name = md.GetString(resource.Name);
            names.Add(name);
            if (!name.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase) || !resource.Implementation.IsNil) continue;

            var block = pe.GetSectionData(resourcesRva + (int)resource.Offset);
            var reader = block.GetReader();
            var length = reader.ReadInt32();
            gResources = (name, reader.ReadBytes(length));
        }
        return (names, gResources);
    }

    [GeneratedRegex(@"^netcoreapp(\d+\.\d+)$")]
    private static partial Regex NetCoreAppRegex();

    // ───────────────────────── Sources ─────────────────────────

    private static void FixSources(string projectDir, List<string> fixes)
    {
        var files = Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        var replacedHelpers = 0;
        var strippedAttributes = 0;
        var strippedGenerators = 0;
        var usesPrivateImpl = false;
        string? privateImplFile = null;

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var original = text;

            if (HelperTypeRegex().Match(text) is { Success: true } helper)
            {
                var canonical = CanonicalHelper(helper.Groups["kind"].Value, helper.Groups["n"].Value);
                if (canonical is not null) { text = canonical; replacedHelpers++; }
            }

            if (PrivateImplDeclarationRegex().IsMatch(text) || Path.GetFileName(file) == "-PrivateImplementationDetails-.cs")
                privateImplFile = file;
            else if (text.Contains("_003CPrivateImplementationDetails_003E.InlineArray"))
                usesPrivateImpl = true;

            var stripped = ReservedAttributeRegex().Replace(text, "");
            if (stripped != text) { strippedAttributes++; text = stripped; }

            // The generator output is already decompiled into the project, so the trigger attributes must go.
            stripped = SourceGeneratorAttributeRegex().Replace(text, "");
            if (stripped != text) { strippedGenerators++; text = stripped; }

            if (text != original) File.WriteAllText(file, text);
        }

        if (usesPrivateImpl)
        {
            var target = privateImplFile ?? Path.Combine(projectDir, "-PrivateImplementationDetails-.cs");
            var existing = File.Exists(target) ? File.ReadAllText(target) : "";
            if (!existing.Contains("InlineArrayElementRef"))
            {
                if (!PrivateImplDeclarationRegex().IsMatch(existing))
                    File.WriteAllText(target, PrivateImplementationDetails);
                else
                {
                    var close = existing.LastIndexOf('}');
                    File.WriteAllText(target, existing[..close] + InlineArrayHelpers + existing[close..]);
                }
                fixes.Add("added inline-array helpers to <PrivateImplementationDetails>");
            }
        }

        if (replacedHelpers > 0) fixes.Add($"rewrote {replacedHelpers} compiler-generated collection/inline-array type(s)");
        if (strippedAttributes > 0) fixes.Add("removed compiler-reserved attributes (RefSafetyRules etc.)");
        if (strippedGenerators > 0) fixes.Add($"removed source-generator attributes in {strippedGenerators} file(s) ([GeneratedRegex], [LibraryImport])");
    }

    [GeneratedRegex(@"^[ \t]*(?:\[[^\r\n]*\][ \t]*)*(?:internal\s+|public\s+)?(?:sealed\s+)?(?:class|struct)\s+_003C_003E[yz]__(?<kind>ReadOnlyArray|ReadOnlySingleElementList|ReadOnlyList|InlineArray)(?<n>\d*)<T>", RegexOptions.Multiline)]
    private static partial Regex HelperTypeRegex();

    [GeneratedRegex(@"^[ \t]*(?:internal\s+)?(?:static\s+)?(?:sealed\s+)?class\s+_003CPrivateImplementationDetails_003E\b", RegexOptions.Multiline)]
    private static partial Regex PrivateImplDeclarationRegex();

    [GeneratedRegex(@"^[ \t]*\[(?:module|assembly):\s*(?:System\.Runtime\.CompilerServices\.)?(?:RefSafetyRules|NullablePublicOnly|NullableContext|Nullable)(?:Attribute)?\b[^\]]*\][ \t]*\r?\n?", RegexOptions.Multiline)]
    private static partial Regex ReservedAttributeRegex();

    [GeneratedRegex(@"^[ \t]*\[(?:global::)?(?:System\.Text\.RegularExpressions\.|System\.Runtime\.InteropServices\.)?(?:GeneratedRegex|LibraryImport)(?:Attribute)?\((?:[^()""\]]|""(?:[^""\\]|\\.)*""|\([^()]*\))*\)\][ \t]*\r?\n?", RegexOptions.Multiline)]
    private static partial Regex SourceGeneratorAttributeRegex();

    private static string? CanonicalHelper(string kind, string n) => kind switch
    {
        "ReadOnlyArray" => CollectionHelper("_003C_003Ez__ReadOnlyArray", "T[] items", "items"),
        "ReadOnlyList" => CollectionHelper("_003C_003Ez__ReadOnlyList", "List<T> list", "list"),
        "ReadOnlySingleElementList" => CollectionHelper("_003C_003Ez__ReadOnlySingleElementList", "T item", "new T[1] { item }"),
        "InlineArray" when n.Length > 0 => $$"""
            using System.Runtime.CompilerServices;

            [InlineArray({{n}})]
            internal struct _003C_003Ey__InlineArray{{n}}<T>
            {
            	private T _element0;
            }

            """,
        _ => null
    };

    // ReadOnlyCollection<T> implements every interface the compiler's helper types do
    // (IReadOnlyList<T>, IList<T>, IList, ICollection …), so call sites keep compiling unchanged.
    private static string CollectionHelper(string name, string parameter, string argument) => $$"""
        using System.Collections.Generic;
        using System.Collections.ObjectModel;

        internal sealed class {{name}}<T> : ReadOnlyCollection<T>
        {
        	public {{name}}({{parameter}}) : base({{argument}})
        	{
        	}
        }

        """;

    private const string InlineArrayHelpers = """

        	internal static ref TElement InlineArrayElementRef<TBuffer, TElement>(ref TBuffer buffer, int index) where TBuffer : struct
        		=> ref System.Runtime.CompilerServices.Unsafe.Add(ref System.Runtime.CompilerServices.Unsafe.As<TBuffer, TElement>(ref buffer), index);

        	internal static ref TElement InlineArrayFirstElementRef<TBuffer, TElement>(ref TBuffer buffer) where TBuffer : struct
        		=> ref System.Runtime.CompilerServices.Unsafe.As<TBuffer, TElement>(ref buffer);

        	internal static System.Span<TElement> InlineArrayAsSpan<TBuffer, TElement>(ref TBuffer buffer, int length) where TBuffer : struct
        		=> System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref System.Runtime.CompilerServices.Unsafe.As<TBuffer, TElement>(ref buffer), length);

        	internal static System.ReadOnlySpan<TElement> InlineArrayAsReadOnlySpan<TBuffer, TElement>(in TBuffer buffer, int length) where TBuffer : struct
        		=> System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(
        			ref System.Runtime.CompilerServices.Unsafe.As<TBuffer, TElement>(ref System.Runtime.CompilerServices.Unsafe.AsRef(in buffer)), length);

        """;

    private static readonly string PrivateImplementationDetails =
        "internal static class _003CPrivateImplementationDetails_003E\n{" + InlineArrayHelpers + "}\n";
}
