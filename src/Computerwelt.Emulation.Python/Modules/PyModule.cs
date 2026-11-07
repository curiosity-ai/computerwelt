using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>A module: a named namespace of values the host has chosen to expose.</summary>
/// <remarks>
/// Modules are built here rather than parsed from Python source, so the set of importable
/// names is a closed, auditable list. A script cannot reach anything that is not in it.
/// </remarks>
public sealed class PyModuleObject : PyObject
{
    private readonly Dictionary<string, PyObject> _members = new(StringComparer.Ordinal);

    /// <summary>Creates an empty module.</summary>
    public PyModuleObject(string name) => Name = name;

    /// <summary>The module's name.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public override string TypeName => "module";

    /// <inheritdoc />
    public override string Repr() => $"<module '{Name}'>";

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) =>
        name == "__name__" ? new PyStr(Name) : _members.GetValueOrDefault(name);

    /// <summary>Adds a member.</summary>
    public PyModuleObject Add(string name, PyObject value)
    {
        _members[name] = value;
        return this;
    }

    /// <summary>Adds a function taking positional arguments.</summary>
    public PyModuleObject Add(string name, Func<PyObject[], PyObject> implementation) =>
        Add(name, new PyBuiltinFunction($"{Name}.{name}", implementation));

    /// <summary>Adds a function taking positional and keyword arguments.</summary>
    public PyModuleObject Add(string name, Func<PyObject[], PyDict?, PyObject> implementation) =>
        Add(name, new PyBuiltinFunction($"{Name}.{name}", implementation));
}

/// <summary>
/// A package whose submodules are modules in their own right — <c>urllib</c>, holding
/// <c>urllib.parse</c>.
/// </summary>
/// <remarks>
/// A submodule is an attribute of its package once it has been imported, as in CPython:
/// <c>import urllib.parse</c> resolves the submodule, and this package then answers
/// <c>urllib.parse</c> from the machine's module table. A submodule written in Python is
/// built on first import, so the package cannot hold it from the start.
/// </remarks>
public sealed class PyPackageObject(string name, VirtualMachine machine) : PyObject
{
    /// <summary>The package's name.</summary>
    public string Name { get; } = name;

    /// <inheritdoc />
    public override string TypeName => "module";

    /// <inheritdoc />
    public override string Repr() => $"<module '{Name}'>";

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) =>
        name == "__name__" ? new PyStr(Name) : machine.Modules.GetValueOrDefault($"{Name}.{name}");
}
