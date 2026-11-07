using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// <c>_warnings</c>: the one thing <c>warnings</c> (written in Python) cannot find out for
/// itself — where the code that called it is.
/// </summary>
/// <remarks>
/// There is no <c>sys._getframe</c> in the sandbox, so this answers exactly the question
/// <c>warn(..., stacklevel=n)</c> asks and nothing more: the file name, line and module name
/// of a frame further up the stack. It reveals nothing a traceback would not.
/// </remarks>
internal static class WarningsSupport
{
    /// <summary>Builds the hidden module.</summary>
    public static PyModuleObject Create(VirtualMachine machine)
    {
        var module = new PyModuleObject("_warnings");

        // location(depth): the frame `depth` levels above the one calling this function.
        module.Add("location", arguments =>
        {
            var depth = arguments.Length > 0
                ? (int)System.Numerics.BigInteger.Clamp(Builtins.BuiltinNamespace.RequireInt(arguments[0], "location"), 0, int.MaxValue)
                : 1;
            var stack = machine.CallStack;
            var index = stack.Count - 1 - depth;

            if (stack.Count == 0)
            {
                return new PyTuple([new PyStr("sys"), PyInt.From(1), new PyStr("sys")]);
            }

            // A stack level beyond the outermost frame is attributed to `sys`, as CPython does.
            if (index < 0)
            {
                return new PyTuple([new PyStr("sys"), PyInt.From(1), new PyStr("sys")]);
            }

            var frame = stack[index];
            var name = frame.Globals.TryGetValue(new PyStr("__name__"), out var moduleName) ? moduleName : new PyStr("__main__");
            return new PyTuple([new PyStr(frame.Code.FileName), PyInt.From(frame.CurrentLine), name]);
        });

        return module;
    }
}
