namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// Attribute lookup, which for built-in types means finding a method.
/// </summary>
/// <remarks>
/// Methods on built-in types are produced on demand as <see cref="PyBoundMethod"/> values
/// rather than stored in per-type dictionaries. That keeps every type's method table in one
/// readable switch and avoids allocating hundreds of objects at startup for methods a given
/// script will never call.
/// </remarks>
public static class Attributes
{
    /// <summary>Reads an attribute, raising <c>AttributeError</c> when it does not exist.</summary>
    public static PyObject Get(VirtualMachine machine, PyObject target, string name)
    {
        // A user-defined object answers for itself first.
        if (target.GetAttribute(name) is { } own)
        {
            return own;
        }

        if (BuiltinMethods.TryBind(machine, target, name) is { } method)
        {
            return method;
        }

        // `__getattr__` is the last resort of an attribute access, after everything else
        // has failed — and is allowed to raise its own AttributeError.
        if (target is PyInstance instance && instance.FallbackAttribute(name) is { } computed)
        {
            return computed;
        }

        // A class reports itself by name — `type object 'bytes'` — rather than as an
        // instance of `type`, which would name every class the same way.
        throw new PyRaise(target is PyCallable { TypeName: "type" } type
            ? new PyException(
                PyExceptionType.AttributeError,
                $"type object '{type.Name}' has no attribute '{name}'")
            : PyErrors.AttributeError(target.TypeName, name));
    }

    /// <summary>Reads an attribute, returning null instead of raising.</summary>
    public static PyObject? TryGet(VirtualMachine machine, PyObject target, string name)
    {
        if ((target.GetAttribute(name) ?? BuiltinMethods.TryBind(machine, target, name)) is { } found)
        {
            return found;
        }

        if (target is not PyInstance instance)
        {
            return null;
        }

        try
        {
            return instance.FallbackAttribute(name);
        }
        catch (PyRaise raise) when (raise.Exception.IsInstanceOf(PyExceptionType.AttributeError))
        {
            return null;
        }
    }
}
