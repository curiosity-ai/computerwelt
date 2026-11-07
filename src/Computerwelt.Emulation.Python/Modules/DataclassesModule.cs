using System.Runtime.CompilerServices;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>The <c>dataclasses</c> module.</summary>
/// <remarks>
/// <para>
/// <c>@dataclass</c> is a decorator that rewrites a class, and the class already exists as
/// a <see cref="PyClass"/> by the time the decorator runs — so the transformation happens
/// here at run time rather than in the compiler, exactly as CPython does it.
/// </para>
/// <para>
/// Beyond upstream: <c>field()</c> with every option (<c>default_factory</c> above all),
/// fields inherited from dataclass bases, <c>__post_init__</c>, <c>order=</c>,
/// <c>frozen=</c> enforced on assignment, <c>kw_only</c>, <c>__match_args__</c>, and the
/// recursive <c>asdict</c>/<c>astuple</c> plus <c>replace</c>.
/// </para>
/// </remarks>
public static class DataclassesModule
{
    /// <summary>The fields a decorated class has, kept beside it for subclasses to inherit.</summary>
    private static readonly ConditionalWeakTable<PyClass, List<Field>> FieldTable = new();

    /// <summary><c>dataclasses.MISSING</c>.</summary>
    private static readonly PyObject Missing = new Sentinel("MISSING");

    /// <summary><c>dataclasses.KW_ONLY</c>.</summary>
    private static readonly PyObject KeywordOnly = new Sentinel("KW_ONLY");

    /// <summary><c>dataclasses.FrozenInstanceError</c>, an <c>AttributeError</c>.</summary>
    public static PyExceptionType FrozenInstanceError { get; } =
        PyExceptionType.DefineHostException("FrozenInstanceError", PyExceptionType.AttributeError);

    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create(VirtualMachine machine)
    {
        var module = new PyModuleObject("dataclasses");

        module.Add("dataclass", new PyBuiltinFunction("dataclass", (arguments, keywords) =>
        {
            var options = Options.From(keywords);

            // `@dataclass` and `@dataclass(frozen=True)` are both valid, so a call with no
            // class argument returns a decorator rather than a class.
            if (arguments.Length == 0)
            {
                return new PyBuiltinFunction("dataclass", inner => Decorate(machine, inner[0], options));
            }

            return Decorate(machine, arguments[0], options);
        }));

        module.Add("field", new PyBuiltinFunction("field", static (arguments, keywords) =>
        {
            if (arguments.Length > 0)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"field() takes 0 positional arguments but {arguments.Length} were given"));
            }

            PyObject? Keyword(string name) =>
                keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? value : null;

            foreach (var (key, _) in keywords?.Entries ?? [])
            {
                if (key.Display() is not ("default" or "default_factory" or "init" or "repr" or "hash"
                    or "compare" or "metadata" or "kw_only" or "doc"))
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"field() got an unexpected keyword argument '{key.Display()}'"));
                }
            }

            var spec = new FieldSpec
            {
                Default = Keyword("default") is { } d && !ReferenceEquals(d, Missing) ? d : null,
                DefaultFactory = Keyword("default_factory") is { } f && !ReferenceEquals(f, Missing) ? f : null,
                Init = Keyword("init")?.IsTruthy() ?? true,
                Shown = Keyword("repr")?.IsTruthy() ?? true,
                Compare = Keyword("compare")?.IsTruthy() ?? true,
                Hash = Keyword("hash") is { } h and not PyNone ? h.IsTruthy() : null,
                KwOnly = Keyword("kw_only") is { } k and not PyNone ? k.IsTruthy() : null,
                Metadata = Keyword("metadata") is { } m and not PyNone ? m : new PyDict(),
            };

            if (spec.Default is not null && spec.DefaultFactory is not null)
            {
                throw new PyRaise(PyErrors.ValueError("cannot specify both default and default_factory"));
            }

            return spec;
        }));

        module.Add("MISSING", Missing);
        module.Add("KW_ONLY", KeywordOnly);
        module.Add("FrozenInstanceError", FrozenInstanceError);
        module.Add("InitVar", new TypeAlias("InitVar"));

        module.Add("is_dataclass", static arguments =>
        {
            Arity.Exact("is_dataclass", arguments, 1);

            return PyBool.Of(arguments[0] switch
            {
                PyDataclass => true,
                PyClass type => type.GetAttribute("__dataclass_fields__") is not null,

                // An instance of a dataclass is one too, which is what makes the usual
                // `is_dataclass(obj) and not isinstance(obj, type)` idiom work.
                PyInstance instance => instance.Class.GetAttribute("__dataclass_fields__") is not null,
                _ => false,
            });
        });

        module.Add("asdict", new PyBuiltinFunction("asdict", (arguments, keywords) =>
        {
            Arity.Exact("asdict", arguments, 1);
            var factory = keywords is not null && keywords.TryGetValue(new PyStr("dict_factory"), out var given) ? given : null;
            RequireInstance(arguments[0], "asdict");
            return Convert(machine, arguments[0], asTuple: false, factory);
        }));

        module.Add("astuple", new PyBuiltinFunction("astuple", (arguments, keywords) =>
        {
            Arity.Exact("astuple", arguments, 1);
            var factory = keywords is not null && keywords.TryGetValue(new PyStr("tuple_factory"), out var given) ? given : null;
            RequireInstance(arguments[0], "astuple");
            return Convert(machine, arguments[0], asTuple: true, factory);
        }));

        module.Add("replace", new PyBuiltinFunction("replace", (arguments, keywords) =>
        {
            Arity.Exact("replace", arguments, 1);

            if (arguments[0] is PyDataclass record)
            {
                return record.GetAttribute("_replace") is { } replace ? machine.Call(replace, [], keywords) : record;
            }

            if (arguments[0] is not PyInstance instance || FieldsOf(instance.Class) is not { } fields)
            {
                throw new PyRaise(PyErrors.TypeError("replace() should be called on dataclass instances"));
            }

            var changes = new PyDict();

            foreach (var field in fields)
            {
                if (keywords is not null && keywords.TryGetValue(new PyStr(field.Name), out var changed))
                {
                    if (!field.Init)
                    {
                        throw new PyRaise(PyErrors.ValueError(
                            $"field {field.Name} is declared with init=False, it cannot be specified with replace()"));
                    }

                    changes.Set(new PyStr(field.Name), changed);
                }
                else if (field.Init)
                {
                    changes.Set(new PyStr(field.Name), Read(instance, field.Name));
                }
            }

            foreach (var (key, _) in keywords?.Entries ?? [])
            {
                if (fields.All(field => field.Name != key.Display()))
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"{instance.Class.Name}.__init__() got an unexpected keyword argument '{key.Display()}'"));
                }
            }

            return machine.Call(instance.Class, [], changes);
        }));

        module.Add("fields", static arguments =>
        {
            Arity.Exact("fields", arguments, 1);

            var declared = arguments[0] switch
            {
                PyClass type => type.GetAttribute("__dataclass_fields__"),
                PyInstance owner => owner.Class.GetAttribute("__dataclass_fields__"),
                _ => null,
            };

            if (declared is PyDict descriptors)
            {
                return new PyTuple([.. descriptors.Entries.Select(static entry => entry.Value)]);
            }

            if (arguments[0] is PyDataclass dataclass)
            {
                return new PyTuple([.. dataclass.FieldNames.Select(static name =>
                    (PyObject)new PyDataclass("Field", ["name"], [new PyStr(name)], frozen: true))]);
            }

            throw new PyRaise(PyErrors.TypeError("must be called with a dataclass type or instance"));
        });

        return module;
    }

    private static void RequireInstance(PyObject value, string function)
    {
        if (value is PyDataclass || (value is PyInstance instance && FieldsOf(instance.Class) is not null))
        {
            return;
        }

        throw new PyRaise(PyErrors.TypeError($"{function}() should be called on dataclass instances"));
    }

    /// <summary>The decorator's switches.</summary>
    private sealed record Options(
        bool Init,
        bool Repr,
        bool Eq,
        bool Order,
        bool UnsafeHash,
        bool Frozen,
        bool MatchArgs,
        bool KwOnly)
    {
        public static Options From(PyDict? keywords)
        {
            bool Flag(string name, bool fallback) =>
                keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? value.IsTruthy() : fallback;

            foreach (var (key, _) in keywords?.Entries ?? [])
            {
                if (key.Display() is not ("init" or "repr" or "eq" or "order" or "unsafe_hash" or "frozen"
                    or "match_args" or "kw_only" or "slots" or "weakref_slot"))
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"dataclass() got an unexpected keyword argument '{key.Display()}'"));
                }
            }

            var options = new Options(
                Flag("init", true),
                Flag("repr", true),
                Flag("eq", true),
                Flag("order", false),
                Flag("unsafe_hash", false),
                Flag("frozen", false),
                Flag("match_args", true),
                Flag("kw_only", false));

            if (options.Order && !options.Eq)
            {
                throw new PyRaise(PyErrors.ValueError("eq must be true if order is true"));
            }

            return options;
        }
    }

    /// <summary>One field of a dataclass.</summary>
    private sealed record Field(
        string Name,
        PyObject Annotation,
        PyObject? Default,
        PyObject? DefaultFactory,
        bool Init,
        bool Repr,
        bool Compare,
        bool? Hash,
        bool KwOnly,
        PyObject Metadata)
    {
        public bool HasDefault => Default is not null || DefaultFactory is not null;
    }

    private static List<Field>? FieldsOf(PyClass type)
    {
        foreach (var owner in type.MroList)
        {
            if (FieldTable.TryGetValue(owner, out var fields))
            {
                return fields;
            }
        }

        return null;
    }

    /// <summary>
    /// Turns a plain class into a dataclass: gives it an <c>__init__</c>, a <c>__repr__</c>
    /// and an <c>__eq__</c> over its fields, and whatever else the switches ask for.
    /// </summary>
    /// <remarks>
    /// The fields are the inherited dataclass fields followed by the class's own
    /// annotations, in declaration order, minus the <c>ClassVar</c>s — which is exactly how
    /// CPython decides, and why annotations are retained at run time at all. A method the
    /// class body defines itself is never replaced.
    /// </remarks>
    private static PyObject Decorate(VirtualMachine machine, PyObject target, Options options)
    {
        if (target is not PyClass type)
        {
            return target;
        }

        var fields = new List<Field>();

        // Frozen-ness has to agree with every dataclass base, as CPython insists.
        var dataclassBases = type.Mro.Skip(1).Where(static b => FieldTable.TryGetValue(b, out _)).ToList();

        if (dataclassBases.Count > 0)
        {
            var frozenBases = dataclassBases.Count(static b => b.Lookup("__dataclass_frozen__")?.IsTruthy() == true);

            if (frozenBases > 0 && !options.Frozen)
            {
                throw new PyRaise(PyErrors.TypeError("cannot inherit non-frozen dataclass from a frozen one"));
            }

            if (frozenBases < dataclassBases.Count && options.Frozen)
            {
                throw new PyRaise(PyErrors.TypeError("cannot inherit frozen dataclass from a non-frozen one"));
            }
        }

        // Inherited fields first, in base-to-derived order, each base's list once.
        for (var i = type.Mro.Count - 1; i >= 1; i--)
        {
            if (FieldTable.TryGetValue(type.Mro[i], out var inherited))
            {
                foreach (var field in inherited)
                {
                    var existing = fields.FindIndex(f => f.Name == field.Name);

                    if (existing >= 0)
                    {
                        fields[existing] = field;
                    }
                    else
                    {
                        fields.Add(field);
                    }
                }
            }
        }

        var keywordOnly = options.KwOnly;

        if (type.Members.TryGetValue(new PyStr("__annotations__"), out var declared) && declared is PyDict annotations)
        {
            foreach (var (key, annotation) in annotations.Entries)
            {
                var name = key.Display();

                if (IsClassVar(annotation) || IsInitVar(annotation))
                {
                    continue;
                }

                if (IsKeywordOnlyMarker(annotation))
                {
                    keywordOnly = true;
                    continue;
                }

                var field = BuildField(type, name, annotation, keywordOnly);
                var existing = fields.FindIndex(f => f.Name == name);

                if (existing >= 0)
                {
                    fields[existing] = field;
                }
                else
                {
                    fields.Add(field);
                }
            }
        }

        // A field without a default may not follow one with a default, among those the
        // generated `__init__` takes positionally.
        var positional = fields.Where(static f => f.Init && !f.KwOnly).ToList();

        for (var i = 1; i < positional.Count; i++)
        {
            if (!positional[i].HasDefault && positional.Take(i).Any(static f => f.HasDefault))
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"non-default argument '{positional[i].Name}' follows default argument '{positional.Take(i).Last(static f => f.HasDefault).Name}'"));
            }
        }

        FieldTable.AddOrUpdate(type, fields);

        var descriptors = new PyDict();

        foreach (var field in fields)
        {
            descriptors.Set(new PyStr(field.Name), Describe(field));
        }

        type.SetAttribute("__dataclass_fields__", descriptors);
        type.SetAttribute("__dataclass_frozen__", PyBool.Of(options.Frozen));

        bool Own(string name) => type.Members.TryGetValue(new PyStr(name), out _);

        // What the generated `__init__` binds, worked out once rather than on every call.
        var initialized = fields.Where(static f => f.Init).ToList();
        var positionalFields = initialized.Where(static f => !f.KwOnly).ToList();

        if (options.Init && !Own("__init__"))
        {
            type.SetAttribute("__init__", Method(type, "__init__", (arguments, keywords) =>
                Initialize(machine, type, fields, initialized, positionalFields, arguments, keywords)));
        }

        var shown = fields.Where(static f => f.Repr).Select(static f => f.Name).ToList();
        var compared = fields.Where(static f => f.Compare).Select(static f => f.Name).ToList();

        if (options.Repr && !Own("__repr__"))
        {
            type.SetAttribute("__repr__", Method(type, "__repr__", (arguments, _) =>
                new PyStr(Render(machine, type, shown, arguments[0]))));
        }

        if (options.Eq && !Own("__eq__"))
        {
            type.SetAttribute("__eq__", Method(type, "__eq__", (arguments, _) =>
                Equal(machine, type, compared, arguments[0], arguments[1])));
        }

        if (options.Order)
        {
            foreach (var (dunder, op) in new[] { ("__lt__", "<"), ("__le__", "<="), ("__gt__", ">"), ("__ge__", ">=") })
            {
                if (Own(dunder))
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"Cannot overwrite attribute {dunder} in class {type.Name}. Consider using functools.total_ordering"));
                }

                type.SetAttribute(dunder, Method(type, dunder, (arguments, _) =>
                    Order(type, compared, arguments[0], arguments[1], op)));
            }
        }

        // CPython's table: a hash is generated for `unsafe_hash`, or for `eq` with `frozen`;
        // `eq` without `frozen` makes instances unhashable, unless the class wrote its own.
        var hashed = fields.Where(static f => f.Hash ?? f.Compare).Select(static f => f.Name).ToList();

        if (!Own("__hash__"))
        {
            if (options.UnsafeHash || (options.Eq && options.Frozen))
            {
                type.SetAttribute("__hash__", Method(type, "__hash__", (arguments, _) =>
                    PyInt.From(HashOf(hashed, arguments[0]))));
            }
            else if (options.Eq)
            {
                type.SetAttribute("__hash__", PyNone.Instance);
            }
        }
        else if (options.UnsafeHash)
        {
            throw new PyRaise(PyErrors.TypeError($"Cannot overwrite attribute __hash__ in class {type.Name}"));
        }

        if (options.Frozen)
        {
            type.SetAttribute("__setattr__", Method(type, "__setattr__", (arguments, _) =>
            {
                var name = arguments[1].Display();

                if (arguments[0] is PyInstance instance && ReferenceEquals(instance.Class, type) || fields.Any(f => f.Name == name))
                {
                    throw new PyRaise(new PyException(FrozenInstanceError, $"cannot assign to field '{name}'"));
                }

                ((PyInstance)arguments[0]).RawSetAttribute(name, arguments[2]);
                return PyNone.Instance;
            }));

            type.SetAttribute("__delattr__", Method(type, "__delattr__", (arguments, _) =>
                throw new PyRaise(new PyException(FrozenInstanceError, $"cannot delete field '{arguments[1].Display()}'"))));
        }

        if (options.MatchArgs && !Own("__match_args__"))
        {
            type.SetAttribute("__match_args__", new PyTuple([.. positional.Select(static f => (PyObject)new PyStr(f.Name))]));
        }

        PyClass.NamespaceChanged();
        return type;
    }

    private static PyBuiltinFunction Method(PyClass type, string name, Func<PyObject[], PyDict?, PyObject> body) =>
        new($"{type.Name}.{name}", body) { BindsAsMethod = true, Kind = "function" };

    /// <summary>Reads one annotated name of the class body into a field, and settles its class attribute.</summary>
    private static Field BuildField(PyClass type, string name, PyObject annotation, bool keywordOnly)
    {
        var key = new PyStr(name);
        var hasValue = type.Members.TryGetValue(key, out var value);

        if (hasValue && value is FieldSpec spec)
        {
            // CPython leaves a plain default on the class and removes a `field()` whose
            // value comes from a factory, so the class attribute reads as it would there.
            if (spec.Default is { } specDefault)
            {
                type.Members.Set(key, specDefault);
            }
            else
            {
                type.Members.Remove(key);
            }

            CheckMutable(name, spec.Default);

            return new Field(
                name, annotation, spec.Default, spec.DefaultFactory, spec.Init, spec.Shown, spec.Compare,
                spec.Hash, spec.KwOnly ?? keywordOnly, spec.Metadata);
        }

        if (hasValue)
        {
            CheckMutable(name, value);
        }

        return new Field(name, annotation, hasValue ? value : null, null, true, true, true, null, keywordOnly, new PyDict());
    }

    private static void CheckMutable(string name, PyObject? value)
    {
        if (value is PyList or PyDict or PySet { IsFrozen: false })
        {
            throw new PyRaise(PyErrors.ValueError(
                $"mutable default <class '{value.TypeName}'> for field {name} is not allowed: use default_factory"));
        }
    }

    /// <summary>The <c>Field</c> object <c>fields()</c> returns.</summary>
    private static PyDataclass Describe(Field field) => new(
        "Field",
        ["name", "type", "default", "default_factory", "init", "repr", "compare", "kw_only", "hash", "metadata", "doc"],
        [
            new PyStr(field.Name),
            field.Annotation,
            field.Default ?? Missing,
            field.DefaultFactory ?? Missing,
            PyBool.Of(field.Init),
            PyBool.Of(field.Repr),
            PyBool.Of(field.Compare),
            PyBool.Of(field.KwOnly),
            field.Hash is { } hash ? PyBool.Of(hash) : PyNone.Instance,
            field.Metadata,
            PyNone.Instance,
        ],
        frozen: true);

    /// <summary>True for an annotation spelled <c>ClassVar</c> or <c>x.ClassVar</c>.</summary>
    private static bool IsClassVar(PyObject annotation) => AnnotationText(annotation).Contains("ClassVar", StringComparison.Ordinal);

    private static bool IsInitVar(PyObject annotation) => AnnotationText(annotation).Contains("InitVar", StringComparison.Ordinal);

    private static bool IsKeywordOnlyMarker(PyObject annotation) => AnnotationText(annotation) is "KW_ONLY" or "dataclasses.KW_ONLY";

    private static string AnnotationText(PyObject annotation) => annotation is PyStr text ? text.Value : annotation.Repr();

    /// <summary>Binds the constructor arguments to the fields, as CPython's generated one does.</summary>
    private static PyObject Initialize(
        VirtualMachine machine,
        PyClass type,
        List<Field> fields,
        List<Field> parameters,
        List<Field> ordered,
        PyObject[] arguments,
        PyDict? keywords)
    {
        var instance = arguments[0];
        var positional = arguments.AsSpan(1);
        Dictionary<string, PyObject>? bound = keywords is { Count: > 0 } ? new(StringComparer.Ordinal) : null;

        // Keywords bind first, so an unexpected one is reported before an arity error.
        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var name = key.Display();

            if (parameters.All(field => field.Name != name))
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{type.Name}.__init__() got an unexpected keyword argument '{name}'"));
            }

            var index = ordered.FindIndex(field => field.Name == name);

            if (index >= 0 && index < positional.Length)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{type.Name}.__init__() got multiple values for argument '{name}'"));
            }

            bound![name] = value;
        }

        if (positional.Length > ordered.Count)
        {
            var required = ordered.Count(static field => !field.HasDefault);

            throw new PyRaise(PyErrors.TypeError(required == ordered.Count
                ? $"{type.Name}.__init__() takes {ordered.Count + 1} positional arguments but {positional.Length + 1} were given"
                : $"{type.Name}.__init__() takes from {required + 1} to {ordered.Count + 1} positional arguments "
                  + $"but {positional.Length + 1} were given"));
        }

        List<string>? missing = null;
        List<string>? missingKeywords = null;

        foreach (var field in fields)
        {
            PyObject? value;

            if (!field.Init)
            {
                value = field.DefaultFactory is { } factory ? machine.Call(factory, []) : field.Default;

                if (value is null)
                {
                    continue;
                }
            }
            else
            {
                var index = ordered.IndexOf(field);

                value = index >= 0 && index < positional.Length ? positional[index]
                    : bound is not null && bound.TryGetValue(field.Name, out var keyword) ? keyword
                    : field.DefaultFactory is { } factory ? machine.Call(factory, [])
                    : field.Default;

                if (value is null)
                {
                    if (field.KwOnly)
                    {
                        (missingKeywords ??= []).Add(field.Name);
                    }
                    else
                    {
                        (missing ??= []).Add(field.Name);
                    }

                    continue;
                }
            }

            // Straight into the instance: a frozen class's `__setattr__` refuses, and the
            // generated `__init__` is how its fields get their values at all.
            if (instance is PyInstance target)
            {
                target.RawSetAttribute(field.Name, value);
            }
            else
            {
                instance.SetAttribute(field.Name, value);
            }
        }

        if (missing is { Count: > 0 })
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{type.Name}.__init__() missing {missing.Count} required positional "
                + $"argument{(missing.Count == 1 ? string.Empty : "s")}: {Join(missing)}"));
        }

        if (missingKeywords is { Count: > 0 })
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{type.Name}.__init__() missing {missingKeywords.Count} required keyword-only "
                + $"argument{(missingKeywords.Count == 1 ? string.Empty : "s")}: {Join(missingKeywords)}"));
        }

        if (instance is PyInstance created && created.Class.Lookup("__post_init__") is { } postInit)
        {
            machine.Call(ClassModel.BindToInstance(machine, postInit, created, created.Class), []);
        }

        return PyNone.Instance;
    }

    /// <summary>Joins names the way CPython lists missing arguments: <c>'a', 'b' and 'c'</c>.</summary>
    private static string Join(IReadOnlyList<string> names)
    {
        var quoted = names.Select(static name => $"'{name}'").ToList();

        return quoted.Count == 1
            ? quoted[0]
            : string.Join(", ", quoted.Take(quoted.Count - 1)) + " and " + quoted[^1];
    }

    private static string Render(VirtualMachine machine, PyClass type, List<string> names, PyObject instance)
    {
        _ = machine;

        if (!RecursionGuard.TryEnter(instance))
        {
            return "...";
        }

        try
        {
            var owner = instance is PyInstance created ? created.Class : type;
            var parts = names.Select(name => $"{name}={Read(instance, name).Repr()}");
            return $"{owner.Name}({string.Join(", ", parts)})";
        }
        finally
        {
            RecursionGuard.Exit(instance);
        }
    }

    private static PyObject Equal(VirtualMachine machine, PyClass type, List<string> names, PyObject left, PyObject right)
    {
        _ = type;

        // CPython's generated `__eq__` opens with an identity check, so a cyclic instance
        // still compares equal to itself.
        if (ReferenceEquals(left, right))
        {
            return PyBool.True;
        }

        if (right is not PyInstance other || left is not PyInstance self || !ReferenceEquals(other.Class, self.Class))
        {
            return Builtins.NotImplementedSingleton.Instance;
        }

        // The field walk recurses in the host, so it charges the same depth budget a
        // Python-level recursion would; a cyclic pair otherwise overflows the host stack.
        using var level = machine.EnterRecursion();

        foreach (var name in names)
        {
            // Field comparison uses `==`, which consults `__eq__` even for two references
            // to the same object — unlike container membership, which shortcuts on identity.
            if (!Operators.Compare("==", Read(left, name), Read(right, name)).IsTruthy())
            {
                return PyBool.False;
            }
        }

        return PyBool.True;
    }

    /// <summary>
    /// The generated ordering methods: the fields compared as tuples would be — the first
    /// pair that differs decides — without building the two tuples per comparison.
    /// </summary>
    private static PyObject Order(PyClass type, List<string> names, PyObject left, PyObject right, string op)
    {
        _ = type;

        if (right is not PyInstance other || left is not PyInstance self || !ReferenceEquals(other.Class, self.Class))
        {
            return Builtins.NotImplementedSingleton.Instance;
        }

        foreach (var name in names)
        {
            var a = Read(left, name);
            var b = Read(right, name);

            if (!PyObject.SameOrEqual(a, b))
            {
                return Operators.Compare(op, a, b);
            }
        }

        // Every field equal: the tuples were equal, so only the inclusive orders hold.
        return PyBool.Of(op is "<=" or ">=");
    }
    private static System.Numerics.BigInteger HashOf(List<string> names, PyObject instance) =>
        new PyTuple([.. names.Select(name => Read(instance, name))]).PyHash();

    /// <summary>Reads a field, falling back to the class attribute as attribute access does.</summary>
    private static PyObject Read(PyObject instance, string name) =>
        instance.GetAttribute(name)
        ?? throw new PyRaise(new PyException(
            PyExceptionType.AttributeError,
            $"'{instance.TypeName}' object has no attribute '{name}'"));

    /// <summary>
    /// <c>asdict</c> and <c>astuple</c>: a deep copy that turns every dataclass instance it
    /// meets into a dict (or tuple), and rebuilds lists, tuples and dicts around them.
    /// </summary>
    private static PyObject Convert(VirtualMachine machine, PyObject value, bool asTuple, PyObject? factory)
    {
        using var level = machine.EnterRecursion();

        switch (value)
        {
            case PyInstance instance when FieldsOf(instance.Class) is { } fields:
            {
                if (asTuple)
                {
                    var items = fields.Select(f => Convert(machine, Read(instance, f.Name), asTuple, factory)).ToList();
                    return factory is null ? new PyTuple(items) : machine.Call(factory, [new PyList(items)]);
                }

                var pairs = fields.Select(f => (PyObject)new PyTuple([new PyStr(f.Name), Convert(machine, Read(instance, f.Name), asTuple, factory)])).ToList();

                if (factory is not null)
                {
                    return machine.Call(factory, [new PyList(pairs)]);
                }

                var dict = new PyDict();

                foreach (var pair in pairs.Cast<PyTuple>())
                {
                    dict.Set(pair.Items[0], pair.Items[1]);
                }

                return dict;
            }

            case PyDataclass record:
            {
                if (asTuple)
                {
                    return new PyTuple([.. record.FieldNames.Select(f => Convert(machine, record.GetAttribute(f) ?? PyNone.Instance, asTuple, factory))]);
                }

                var dict = new PyDict();

                foreach (var field in record.FieldNames)
                {
                    dict.Set(new PyStr(field), Convert(machine, record.GetAttribute(field) ?? PyNone.Instance, asTuple, factory));
                }

                return dict;
            }

            case PyNamedTuple named:
                return machine.Call(TypeRegistry.Of(named), [.. named.Items.Select(i => Convert(machine, i, asTuple, factory))]);

            case PyList list:
                return new PyList([.. list.Items.Select(i => Convert(machine, i, asTuple, factory))]);

            case PyTuple tuple:
                return new PyTuple([.. tuple.Items.Select(i => Convert(machine, i, asTuple, factory))]);

            case PyDict dict:
            {
                var copy = dict.Fresh();

                foreach (var (key, item) in dict.Entries)
                {
                    copy.Set(Convert(machine, key, asTuple, factory), Convert(machine, item, asTuple, factory));
                }

                return copy;
            }

            default:
                return value;
        }
    }

    /// <summary>The object <c>field(...)</c> returns, consumed by the decorator.</summary>
    private sealed class FieldSpec : PyObject
    {
        public PyObject? Default { get; init; }

        public PyObject? DefaultFactory { get; init; }

        public bool Init { get; init; } = true;

        public bool Shown { get; init; } = true;

        public bool Compare { get; init; } = true;

        public bool? Hash { get; init; }

        public bool? KwOnly { get; init; }

        public PyObject Metadata { get; init; } = new PyDict();

        public override string TypeName => "Field";

        public override string Repr() =>
            $"Field(name=None,type=None,default={(Default ?? Missing).Repr()},default_factory={(DefaultFactory ?? Missing).Repr()},init={Init},repr={Shown},hash=None,compare={Compare},metadata={Metadata.Repr()},kw_only={(KwOnly is { } k ? k.ToString() : "MISSING")})";
    }

    /// <summary>A named marker such as <c>MISSING</c>.</summary>
    private sealed class Sentinel(string name) : PyObject
    {
        public override string TypeName => $"_{name}_TYPE";

        public override string Repr() => name;

        public override System.Numerics.BigInteger PyHash() => RuntimeHelpers.GetHashCode(this);
    }
}
