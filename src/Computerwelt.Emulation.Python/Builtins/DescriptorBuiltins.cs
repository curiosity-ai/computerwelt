using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Builtins;

/// <summary>
/// <c>property</c>, <c>classmethod</c>, <c>staticmethod</c> and <c>super</c>: the builtins
/// the class model is written with.
/// </summary>
internal static class DescriptorBuiltins
{
    /// <summary>Registers the descriptor builtins.</summary>
    public static void Register(VirtualMachine machine, PyDict builtins)
    {
        builtins.Set(new PyStr("super"), new PyBuiltinFunction("super", arguments =>
        {
            switch (arguments.Length)
            {
                case 2:
                    return arguments[0] is PyClass start
                        ? new PySuper(machine, start, arguments[1])
                        : throw new PyRaise(PyErrors.TypeError(
                            $"super() argument 1 must be a type, not {arguments[0].TypeName}"));

                case 0:
                    throw new PyRaise(PyErrors.RuntimeError("super(): no arguments"));

                default:
                    throw new PyRaise(PyErrors.TypeError("super() takes 0 or 2 arguments here"));
            }
        }));
    }
}
