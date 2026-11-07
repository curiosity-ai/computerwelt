using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// <c>enum</c>: <c>Enum</c>, <c>IntEnum</c>, <c>StrEnum</c>, <c>Flag</c>, <c>IntFlag</c>,
/// <c>auto</c> and <c>unique</c>.
/// </summary>
/// <remarks>
/// <para>
/// In CPython an enum's class-level behaviour — <c>Color(1)</c>, <c>Color['RED']</c>,
/// <c>list(Color)</c>, members that cannot be reassigned — comes from its metaclass. This
/// interpreter has no user metaclasses, so that behaviour is a <see cref="ClassBehaviour"/>
/// written here and inherited by every class deriving from <c>Enum</c>. Everything an
/// enum's body defines besides its members — methods, properties, an <c>__init__</c> that
/// unpacks a tuple value — is an ordinary class member.
/// </para>
/// <para>
/// The module is built per run, so its base classes and every enum derived from them belong
/// to one tenant's program.
/// </para>
/// </remarks>
internal static class EnumModule
{
    public static PyModuleObject Create(VirtualMachine machine)
    {
        var behaviour = new EnumBehaviour(machine);
        var module = new PyModuleObject("enum");

        var enumClass = Base(machine, "Enum", [], behaviour);
        behaviour.EnumClass = enumClass;
        AddEnumMembers(machine, enumClass);

        var intEnum = Base(machine, "IntEnum", [TypeRegistry.Int, enumClass], behaviour);
        AddValueText(intEnum);

        var strEnum = Base(machine, "StrEnum", [TypeRegistry.Str, enumClass], behaviour);
        AddValueText(strEnum);

        var flag = Base(machine, "Flag", [enumClass], behaviour);
        behaviour.FlagClass = flag;
        AddFlagMembers(machine, behaviour, flag);

        var intFlag = Base(machine, "IntFlag", [TypeRegistry.Int, flag], behaviour);
        AddValueText(intFlag);

        var reprEnum = Base(machine, "ReprEnum", [enumClass], behaviour);
        AddValueText(reprEnum);

        module.Add("Enum", enumClass);
        module.Add("IntEnum", intEnum);
        module.Add("StrEnum", strEnum);
        module.Add("Flag", flag);
        module.Add("IntFlag", intFlag);
        module.Add("ReprEnum", reprEnum);

        var enumType = new PyType(
            "EnumType",
            value => value is PyClass type && type.IsSubclassOf(enumClass),
            static (_, _) => throw new PyRaise(PyErrors.TypeError("EnumType cannot be instantiated here")));
        module.Add("EnumType", enumType);
        module.Add("EnumMeta", enumType);

        module.Add("auto", static arguments =>
        {
            Arity.Exact("auto", arguments, 0);
            return new AutoValue();
        });

        module.Add("unique", arguments =>
        {
            Arity.Exact("unique", arguments, 1);

            if (arguments[0] is PyClass type && type.BehaviourState is EnumState state)
            {
                var aliases = state.ByName
                    .Where(entry => !ReferenceEquals(state.Members.FirstOrDefault(m => Name(m) == entry.Key), entry.Value))
                    .Select(entry => $"{entry.Key} -> {Name(entry.Value)}")
                    .ToList();

                if (aliases.Count > 0)
                {
                    throw new PyRaise(PyErrors.ValueError(
                        $"duplicate values found in <enum '{type.Name}'>: {string.Join(", ", aliases)}"));
                }
            }

            return arguments[0];
        });

        return module;
    }

    private static PyClass Base(VirtualMachine machine, string name, IReadOnlyList<PyObject> bases, EnumBehaviour behaviour) =>
        new(name, new PyDict(), bases, machine) { Behaviour = behaviour, BehaviourState = new EnumState() };

    /// <summary>The members of a member: <c>name</c>, <c>value</c> and the protocols.</summary>
    private static void AddEnumMembers(VirtualMachine machine, PyClass type)
    {
        Set(type, "name", new PyProperty(Function("name", static a => FieldOf(a[0], "_name_")), null, null, null));
        Set(type, "value", new PyProperty(Function("value", static a => FieldOf(a[0], "_value_")), null, null, null));
        Set(type, "_name_", new PyProperty(Function("_name_", static a => FieldOf(a[0], "_name_")), null, null, null));
        Set(type, "_value_", new PyProperty(Function("_value_", static a => FieldOf(a[0], "_value_")), null, null, null));

        Set(type, "__repr__", Method("__repr__", static a =>
        {
            var member = (PyInstance)a[0];
            var name = FieldOf(member, "_name_");
            var value = FieldOf(member, "_value_");
            return new PyStr(name is PyNone
                ? $"<{member.Class.Name}: {value.Repr()}>"
                : $"<{member.Class.Name}.{name.Display()}: {value.Repr()}>");
        }));

        Set(type, "__str__", Method("__str__", static a =>
        {
            var member = (PyInstance)a[0];
            var name = FieldOf(member, "_name_");
            return new PyStr(name is PyNone
                ? $"{member.Class.Name}({FieldOf(member, "_value_").Repr()})"
                : $"{member.Class.Name}.{name.Display()}");
        }));

        Set(type, "__format__", Method("__format__", a =>
            new PyStr(StringFormatter.Format(new PyStr(a[0].Display()), a.Length > 1 ? a[1].Display() : string.Empty))));

        Set(type, "__hash__", Method("__hash__", static a => PyInt.From(FieldOf(a[0], "_name_").PyHash())));

        Set(type, "__reduce_ex__", Method("__reduce_ex__", static a =>
            new PyTuple([((PyInstance)a[0]).Class, new PyTuple([FieldOf(a[0], "_value_")])])));

        _ = machine;
    }

    /// <summary>
    /// <c>IntEnum</c>, <c>StrEnum</c> and <c>IntFlag</c> print as their value and hash as it,
    /// so they stand in for the plain value anywhere one is expected.
    /// </summary>
    private static void AddValueText(PyClass type)
    {
        Set(type, "__str__", Method("__str__", static a => new PyStr(ValueOf(a[0]).Display())));
        Set(type, "__format__", Method("__format__", static a =>
            new PyStr(StringFormatter.Format(ValueOf(a[0]), a.Length > 1 ? a[1].Display() : string.Empty))));
        Set(type, "__hash__", Method("__hash__", static a => PyInt.From(ValueOf(a[0]).PyHash())));
    }

    /// <summary>The bitwise protocol of <c>Flag</c>.</summary>
    private static void AddFlagMembers(VirtualMachine machine, EnumBehaviour behaviour, PyClass flag)
    {
        _ = machine;

        PyObject Combine(PyObject[] a, Func<BigInteger, BigInteger, BigInteger> op)
        {
            var self = (PyInstance)a[0];

            if (FlagBits(self, a[1]) is not { } other)
            {
                return Builtins.NotImplementedSingleton.Instance;
            }

            return behaviour.FlagMember(self.Class, op(Bits(self), other));
        }

        Set(flag, "__or__", Method("__or__", a => Combine(a, static (x, y) => x | y)));
        Set(flag, "__ror__", Method("__ror__", a => Combine(a, static (x, y) => x | y)));
        Set(flag, "__and__", Method("__and__", a => Combine(a, static (x, y) => x & y)));
        Set(flag, "__rand__", Method("__rand__", a => Combine(a, static (x, y) => x & y)));
        Set(flag, "__xor__", Method("__xor__", a => Combine(a, static (x, y) => x ^ y)));
        Set(flag, "__rxor__", Method("__rxor__", a => Combine(a, static (x, y) => x ^ y)));

        Set(flag, "__invert__", Method("__invert__", a =>
        {
            var self = (PyInstance)a[0];
            var all = behaviour.State(self.Class).Members.Aggregate(BigInteger.Zero, (bits, m) => bits | Bits(m));
            return behaviour.FlagMember(self.Class, all & ~Bits(self));
        }));

        Set(flag, "__contains__", Method("__contains__", static a =>
        {
            var self = (PyInstance)a[0];

            if (a[1] is not PyInstance other || !other.Class.IsSubclassOf(self.Class))
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"unsupported operand type(s) for 'in': '{a[1].TypeName}' and '{self.Class.Name}'"));
            }

            return PyBool.Of((Bits(self) & Bits(other)) == Bits(other));
        }));

        Set(flag, "__bool__", Method("__bool__", static a => PyBool.Of(!Bits((PyInstance)a[0]).IsZero)));

        Set(flag, "__iter__", Method("__iter__", a =>
        {
            var self = (PyInstance)a[0];
            var bits = Bits(self);
            var parts = behaviour.State(self.Class).Members.Where(m => IsSingleBit(Bits(m)) && (bits & Bits(m)) == Bits(m));
            return new PyIterator(parts.Cast<PyObject>().ToList(), "iterator");
        }));

        Set(flag, "__len__", Method("__len__", a =>
        {
            var self = (PyInstance)a[0];
            var bits = Bits(self);
            return PyInt.From(behaviour.State(self.Class).Members.Count(m => IsSingleBit(Bits(m)) && (bits & Bits(m)) == Bits(m)));
        }));
    }

    private static bool IsSingleBit(BigInteger value) => !value.IsZero && (value & (value - 1)).IsZero;

    private static BigInteger Bits(PyInstance member) =>
        FieldOf(member, "_value_") is PyInt integer ? integer.Value : BigInteger.Zero;

    private static BigInteger? FlagBits(PyInstance self, PyObject other) => other switch
    {
        PyInstance member when member.Class.IsSubclassOf(self.Class) || self.Class.IsSubclassOf(member.Class) => Bits(member),
        // An IntFlag combines with a plain int.
        PyInt integer when self.BuiltinValue is PyInt => integer.Value,
        _ => null,
    };

    private static void Set(PyClass type, string name, PyObject value) => type.Members.Set(new PyStr(name), value);

    private static PyBuiltinFunction Function(string name, Func<PyObject[], PyObject> body) => new(name, body);

    private static PyBuiltinFunction Method(string name, Func<PyObject[], PyObject> body) =>
        new(name, (arguments, _) => body(arguments)) { BindsAsMethod = true, Kind = "function" };

    private static PyObject FieldOf(PyObject member, string name) =>
        member is PyInstance instance && instance.Fields.TryGetValue(new PyStr(name), out var value) ? value : PyNone.Instance;

    private static string? Name(PyInstance member) => FieldOf(member, "_name_") is PyStr name ? name.Value : null;

    private static PyObject ValueOf(PyObject member) => member is PyInstance { BuiltinValue: { } inner } ? inner : FieldOf(member, "_value_");

    /// <summary>The members of one enum class.</summary>
    internal sealed class EnumState
    {
        /// <summary>The canonical members, in definition order.</summary>
        public List<PyInstance> Members { get; } = [];

        /// <summary>Every name, aliases included.</summary>
        public Dictionary<string, PyInstance> ByName { get; } = new(StringComparer.Ordinal);

        /// <summary>Combinations of flags made on demand, by value.</summary>
        public Dictionary<BigInteger, PyInstance> Pseudo { get; } = [];
    }

    /// <summary><c>auto()</c>: a value to be generated when the class is built.</summary>
    private sealed class AutoValue : PyObject
    {
        public override string TypeName => "auto";

        public override string Repr() => "<enum.auto>";
    }

    private sealed class EnumBehaviour(VirtualMachine machine) : ClassBehaviour
    {
        public PyClass? EnumClass { get; set; }

        public PyClass? FlagClass { get; set; }

        public EnumState State(PyClass type)
        {
            if (type.BehaviourState is EnumState state && ReferenceEquals(state, type.BehaviourState))
            {
                return state;
            }

            var fresh = new EnumState();
            type.BehaviourState = fresh;
            return fresh;
        }

        private bool IsFlag(PyClass type) => FlagClass is not null && type.IsSubclassOf(FlagClass);

        public override void Created(VirtualMachine running, PyClass type, PyDict keywords)
        {
            _ = keywords;

            foreach (var parent in type.Mro.Skip(1))
            {
                if (parent.BehaviourState is EnumState inherited && inherited.Members.Count > 0)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"<enum '{type.Name}'> cannot extend <enum '{parent.Name}'>"));
                }
            }

            var state = new EnumState();
            type.BehaviourState = state;
            var ignored = type.Members.TryGetValue(new PyStr("_ignore_"), out var ignore)
                ? (ignore is PyStr text ? text.Value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries) : VirtualMachine.RequireIterable(ignore).Select(static i => i.Display()).ToArray())
                : [];

            var lastValues = new List<PyObject>();

            foreach (var (key, raw) in type.Members.Entries.ToList())
            {
                var name = key.Display();

                if (!IsMemberCandidate(name, raw) || ignored.Contains(name))
                {
                    if (ignored.Contains(name))
                    {
                        type.Members.Remove(key);
                    }

                    continue;
                }

                var value = raw is AutoValue ? Generate(type, name, state.Members.Count, lastValues) : raw;
                lastValues.Add(value);

                var member = Build(running, type, name, value);
                var memberValue = FieldOf(member, "_value_");

                var existing = state.Members.FirstOrDefault(m => PyObject.SameOrEqual(FieldOf(m, "_value_"), memberValue));

                if (existing is not null)
                {
                    // A second name for an existing value is an alias, not a new member.
                    state.ByName[name] = existing;
                    type.Members.Set(key, existing);
                    continue;
                }

                state.Members.Add(member);
                state.ByName[name] = member;
                type.Members.Set(key, member);
            }
        }

        private static bool IsMemberCandidate(string name, PyObject value) =>
            !(name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal))
            && !(name.Length > 2 && name[0] == '_' && name[^1] == '_')
            && value is not (PyFunction or PyClassMethod or PyStaticMethod or PyProperty or PyBuiltinFunction or PyClass)
            && !(value is PyInstance descriptor && descriptor.Class.Lookup("__get__") is not null);

        private PyObject Generate(PyClass type, string name, int count, List<PyObject> lastValues)
        {
            if (type.Lookup("_generate_next_value_") is { } custom && custom is not PyBuiltinFunction)
            {
                var function = custom is PyStaticMethod s ? s.Function : custom is PyClassMethod c ? ClassModel.BindCallable(c.Function, type) : custom;
                return machine.Call(function, [new PyStr(name), PyInt.From(1), PyInt.From(count), new PyList([.. lastValues])]);
            }

            if (type.BuiltinBase is { Name: "str" })
            {
                return new PyStr(name.ToLowerInvariant());
            }

            var ints = lastValues.OfType<PyInt>().Select(static i => i.Value).ToList();

            if (IsFlag(type))
            {
                var high = ints.Count == 0 ? BigInteger.Zero : ints.Max();
                return PyInt.From(high.IsZero ? BigInteger.One : BigInteger.One << (int)(high.GetBitLength()));
            }

            return PyInt.From(ints.Count == 0 ? BigInteger.One : ints[^1] + 1);
        }

        /// <summary>Makes one member: the instance, its builtin value, its <c>__init__</c>.</summary>
        private PyInstance Build(VirtualMachine running, PyClass type, string name, PyObject value)
        {
            var member = new PyInstance(type, running);
            var arguments = value is PyTuple tuple ? tuple.Items.ToArray() : [value];

            if (type.BuiltinBase is { } builtin)
            {
                member.BuiltinValue = builtin.Construct(type.Lookup("__init__") is PyFunction ? [value] : arguments, null);
            }

            member.Fields.Set(new PyStr("_name_"), new PyStr(name));
            member.Fields.Set(new PyStr("_value_"), member.BuiltinValue ?? value);

            if (type.Lookup("__init__") is PyFunction initializer)
            {
                running.Call(initializer.Bind(member), arguments);
            }

            return member;
        }

        /// <summary>The member, or the combination of members, with these flag bits.</summary>
        public PyInstance FlagMember(PyClass type, BigInteger bits)
        {
            var state = State(type);

            if (state.Members.FirstOrDefault(m => Bits(m) == bits) is { } canonical)
            {
                return canonical;
            }

            if (state.Pseudo.TryGetValue(bits, out var known))
            {
                return known;
            }

            var parts = state.Members.Where(m => IsSingleBit(Bits(m)) && (bits & Bits(m)) == Bits(m)).ToList();
            var covered = parts.Aggregate(BigInteger.Zero, (all, m) => all | Bits(m));
            var member = new PyInstance(type, machine);

            if (type.BuiltinBase is { } builtin)
            {
                member.BuiltinValue = builtin.Construct([PyInt.From(bits)], null);
            }
            else if (covered != bits)
            {
                throw new PyRaise(PyErrors.ValueError($"{bits} is not a valid {type.Name}"));
            }

            member.Fields.Set(new PyStr("_value_"), PyInt.From(bits));
            member.Fields.Set(new PyStr("_name_"), bits.IsZero || covered != bits
                ? PyNone.Instance
                : new PyStr(string.Join("|", parts.Select(static p => Name(p)))));

            state.Pseudo[bits] = member;
            return member;
        }

        public override PyObject? Call(VirtualMachine running, PyClass type, PyObject[] arguments, PyDict? keywords)
        {
            var state = State(type);

            if (state.Members.Count == 0)
            {
                // `Enum('Color', 'RED GREEN')` — the functional API — builds a new class.
                return arguments.Length >= 2
                    ? Functional(running, type, arguments[0].Display(), arguments[1])
                    : throw new PyRaise(PyErrors.TypeError($"{type.Name}() takes a value of an existing member"));
            }

            if (arguments.Length != 1 || keywords is { Count: > 0 })
            {
                throw new PyRaise(PyErrors.TypeError($"{type.Name}() takes exactly one argument"));
            }

            var value = arguments[0];

            if (value is PyInstance existing && existing.Class.IsSubclassOf(type))
            {
                return existing;
            }

            foreach (var member in state.Members)
            {
                if (PyObject.SameOrEqual(FieldOf(member, "_value_"), value))
                {
                    return member;
                }
            }

            if (IsFlag(type) && value is PyInt bits && bits.Value >= 0)
            {
                return FlagMember(type, bits.Value);
            }

            if (type.Lookup("_missing_") is { } missing && missing is not PyBuiltinFunction)
            {
                var hook = missing is PyClassMethod c ? ClassModel.BindCallable(c.Function, type) : missing is PyStaticMethod s ? s.Function : ClassModel.BindCallable(missing, type);
                var found = running.Call(hook, [value]);

                if (found is not PyNone)
                {
                    return found;
                }
            }

            throw new PyRaise(PyErrors.ValueError($"{value.Repr()} is not a valid {type.Name}"));
        }

        private PyClass Functional(VirtualMachine running, PyClass baseType, string name, PyObject spec)
        {
            var members = new PyDict();

            switch (spec)
            {
                case PyStr names:
                {
                    var index = 1;

                    foreach (var memberName in names.Value.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        members.Set(new PyStr(memberName), PyInt.From(index++));
                    }

                    break;
                }

                case PyDict mapping:
                    foreach (var (key, value) in mapping.Entries)
                    {
                        members.Set(key, value);
                    }

                    break;

                default:
                {
                    var index = 1;

                    foreach (var item in VirtualMachine.RequireIterable(spec))
                    {
                        if (item is PyTuple { Items.Count: 2 } pair)
                        {
                            members.Set(pair.Items[0], pair.Items[1]);
                        }
                        else
                        {
                            members.Set(item, PyInt.From(index++));
                        }
                    }

                    break;
                }
            }

            var type = new PyClass(name, members, [baseType], running);
            Created(running, type, new PyDict());
            return type;
        }

        public override PyObject? GetClassAttribute(PyClass type, string name)
        {
            if (name != "__members__" || type.BehaviourState is not EnumState state)
            {
                return null;
            }

            var mapping = new PyDict();

            foreach (var (memberName, member) in state.ByName)
            {
                mapping.Set(new PyStr(memberName), member);
            }

            return mapping;
        }

        public override bool SetClassAttribute(PyClass type, string name, PyObject value)
        {
            if (type.BehaviourState is EnumState state && state.ByName.ContainsKey(name))
            {
                throw new PyRaise(new PyException(PyExceptionType.AttributeError, $"cannot reassign member '{name}'"));
            }

            return false;
        }

        public override IEnumerable<PyObject>? Iterate(PyClass type) => State(type).Members.Cast<PyObject>().ToList();

        public override int? Length(PyClass type) => State(type).Members.Count;

        public override bool? Contains(PyClass type, PyObject item) =>
            item is PyInstance member && member.Class.IsSubclassOf(type)
                ? true
                : State(type).Members.Any(m => PyObject.SameOrEqual(FieldOf(m, "_value_"), item));

        public override PyObject? GetItem(PyClass type, PyObject index) =>
            State(type).ByName.TryGetValue(index.Display(), out var member) && index is PyStr
                ? member
                : throw new PyRaise(new PyException(PyExceptionType.KeyError, index.Repr(), [index]));

        public override string? ClassRepr(PyClass type) =>
            IsFlag(type) ? $"<flag '{type.Name}'>" : $"<enum '{type.Name}'>";
    }
}
