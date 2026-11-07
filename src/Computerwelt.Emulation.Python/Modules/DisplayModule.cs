using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// Rich output for notebooks: how a value becomes a MIME bundle, and the
/// <c>IPython.display</c> vocabulary programs use to ask for one.
/// </summary>
/// <remarks>
/// <para>
/// The protocol is Jupyter's, because that is what the code people paste into a notebook is
/// written against: an object that defines <c>_repr_html_</c> (a pandas DataFrame, say) is
/// shown as HTML, <c>_repr_markdown_</c> as markdown, and so on, beside the plain
/// <c>repr</c> every value has. The module is named <c>IPython.display</c> for the same
/// reason — <c>from IPython.display import display, HTML</c> is the idiom.
/// </para>
/// <para>
/// Nothing here renders anything. The bundle is data handed to the host; what the host
/// does with HTML a program produced (sanitize it, sandbox it in an iframe) is the host's
/// decision, and the one place it can be made.
/// </para>
/// </remarks>
public static class DisplayModule
{
    /// <summary>The rich representations asked for, by method name and MIME type.</summary>
    private static readonly (string Method, string Mime)[] Representations =
    [
        ("_repr_html_", "text/html"),
        ("_repr_markdown_", "text/markdown"),
        ("_repr_svg_", "image/svg+xml"),
        ("_repr_latex_", "text/latex"),
        ("_repr_json_", "application/json"),
        ("_repr_png_", "image/png"),
        ("_repr_jpeg_", "image/jpeg"),
    ];

    /// <summary>
    /// The MIME bundle for a value: <c>text/plain</c> always, plus every rich
    /// representation the value offers.
    /// </summary>
    /// <remarks>
    /// A representation method that raises is skipped rather than failing the display —
    /// IPython does the same, and the plain text is still worth showing.
    /// </remarks>
    public static Dictionary<string, string> Format(VirtualMachine machine, PyObject value)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(value);

        var bundle = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["text/plain"] = value.Repr(),
        };

        if (Call(machine, value, "_repr_mimebundle_") is PyDict mimeBundle)
        {
            foreach (var (key, data) in mimeBundle.Entries)
            {
                bundle[key.Display()] = Text(machine, key.Display(), data);
            }
        }

        foreach (var (method, mime) in Representations)
        {
            if (bundle.ContainsKey(mime) || Call(machine, value, method) is not { } data || data is PyNone)
            {
                continue;
            }

            bundle[mime] = Text(machine, mime, data);
        }

        return bundle;
    }

    private static PyObject? Call(VirtualMachine machine, PyObject value, string method)
    {
        // Looked up on the object, as IPython does; a class (rather than an instance) whose
        // namespace happens to hold the method is not asking to be displayed richly.
        if (value is PyClass || Attributes.TryGet(machine, value, method) is not { } bound)
        {
            return null;
        }

        try
        {
            return machine.Call(bound, []);
        }
        catch (PyRaise)
        {
            return null;
        }
    }

    /// <summary>Turns one representation into the string the bundle carries.</summary>
    private static string Text(VirtualMachine machine, string mime, PyObject data) => (mime, data) switch
    {
        // Binary images travel base64-encoded, as in a Jupyter message.
        (_, PyBytes bytes) => Convert.ToBase64String(bytes.Value),
        ("application/json", not PyStr) => machine.Import("json") is { } json
            && json.GetAttribute("dumps") is { } dumps
            ? machine.Call(dumps, [data]).Display()
            : data.Repr(),
        _ => data.Display(),
    };

    /// <summary>
    /// Builds <c>IPython.display</c> over a machine whose <see cref="VirtualMachine.DisplayHook"/>
    /// receives what <c>display()</c> is given.
    /// </summary>
    public static PyModuleObject Create(VirtualMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        var module = new PyModuleObject("IPython.display");

        module.Add("display", CreateDisplay(machine));

        module.Add("clear_output", new PyBuiltinFunction("clear_output", (arguments, keywords) =>
        {
            _ = arguments;
            _ = keywords;
            machine.DisplayHook?.Invoke(PyNone.Instance, ClearOutput);
            return PyNone.Instance;
        }));

        AddKind(module, "HTML", "text/html");
        AddKind(module, "Markdown", "text/markdown");
        AddKind(module, "SVG", "image/svg+xml");
        AddKind(module, "Latex", "text/latex");
        AddKind(module, "Math", "text/latex");
        AddKind(module, "Pretty", "text/plain");
        AddKind(module, "JSON", "application/json");
        AddKind(module, "Javascript", "application/javascript");

        module.Add("Image", new PyBuiltinFunction("Image", (arguments, keywords) =>
        {
            var data = arguments.Length > 0 ? arguments[0] : Keyword(keywords, "data");
            var format = Keyword(keywords, "format")?.Display() ?? (arguments.Length > 2 ? arguments[2].Display() : "png");

            var bytes = data switch
            {
                PyBytes raw => raw.Value,
                PyByteArray array => [.. array.Value],
                _ => throw new PyRaise(PyErrors.TypeError(
                    "Image() takes the image bytes as data=; reading a file or a URL is not available here")),
            };

            var mime = format.ToLowerInvariant() switch
            {
                "png" => "image/png",
                "jpeg" or "jpg" => "image/jpeg",
                "gif" => "image/gif",
                "svg" => "image/svg+xml",
                var other => throw new PyRaise(PyErrors.ValueError($"Cannot embed the '{other}' image format")),
            };

            return new RichObject("Image", mime, new PyBytes(bytes));
        }));

        return module;
    }

    /// <summary>The bundle <c>clear_output()</c> sends, distinguished by its key.</summary>
    public static IReadOnlyDictionary<string, string> ClearOutput { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["application/x-clear-output"] = string.Empty };

    /// <summary><c>display(*objs, raw=False)</c>.</summary>
    public static PyBuiltinFunction CreateDisplay(VirtualMachine machine) =>
        new("display", (arguments, keywords) =>
        {
            var raw = Keyword(keywords, "raw")?.IsTruthy() ?? false;

            foreach (var value in arguments)
            {
                if (raw && value is PyDict bundle)
                {
                    machine.DisplayHook?.Invoke(value, bundle.Entries.ToDictionary(
                        static e => e.Key.Display(), static e => e.Value.Display(), StringComparer.Ordinal));
                    continue;
                }

                machine.DisplayHook?.Invoke(value, null);
            }

            return PyNone.Instance;
        });

    private static void AddKind(PyModuleObject module, string name, string mime) =>
        module.Add(name, new PyBuiltinFunction(name, (arguments, keywords) =>
        {
            var data = arguments.Length > 0 ? arguments[0] : Keyword(keywords, "data") ?? new PyStr(string.Empty);
            return new RichObject(name, mime, data);
        }));

    private static PyObject? Keyword(PyDict? keywords, string name) =>
        keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? value : null;

    /// <summary>An <c>HTML(...)</c>, <c>Markdown(...)</c>, … : data tagged with its MIME type.</summary>
    private sealed class RichObject(string kind, string mime, PyObject data) : PyObject
    {
        public override string TypeName => kind;

        public override string Repr() =>
            mime == "text/plain" ? data.Display() : $"<IPython.core.display.{kind} object>";

        public override PyObject? GetAttribute(string name)
        {
            if (name == "data")
            {
                return data;
            }

            return name == "_repr_mimebundle_"
                ? new PyBuiltinFunction(name, _ =>
                {
                    var bundle = new PyDict();
                    bundle.Set(new PyStr(mime), data);
                    return bundle;
                })
                : null;
        }
    }

    /// <summary>Escapes text for an HTML fragment.</summary>
    internal static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '<' => "&lt;",
                '>' => "&gt;",
                '&' => "&amp;",
                '"' => "&quot;",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }
}
