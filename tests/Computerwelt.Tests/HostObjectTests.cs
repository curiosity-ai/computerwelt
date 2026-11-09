using Computerwelt.Emulation.Bash;
using Computerwelt.Emulation.Python;
using Computerwelt.Emulation.Python.HostObjects;
using Computerwelt.Emulation.Python.Runtime;
using Xunit;

namespace Computerwelt.Tests;

/// <summary>A host object reached from Python: the bridge, its policy, and completion through it.</summary>
public sealed class HostObjectTests
{
    private static readonly HostObjectPolicy Policy = HostObjectPolicy.ForAssemblies([typeof(HostObjectTests).Assembly], hiddenMembers: ["Internals"], textTypes: t => t == typeof(Ident));

    private static PythonSession Open(Scope scope)
    {
        var library = PythonLibrary.FromSource(
            "host",
            "Store = _scope.Store\nQ = _scope.Q\nAddAsync = _scope.AddAsync\n",
            _ => new Dictionary<string, PyObject> { ["_scope"] = PyHostObject.Wrap(scope, Policy) });

        var session = new PythonOptions { Libraries = [library] }.StartSession(new InMemoryFileSystem(), "/");

        session.Initialize("from host import *");

        return session;
    }

    [Fact]
    public void Initialize_puts_names_in_scope_without_counting_as_a_cell()
    {
        using var session = Open(new Scope());

        Assert.Equal(0, session.ExecutionCount);
        Assert.Equal("1", session.Run("1").Outputs[0].Text);
        Assert.Equal(1, session.ExecutionCount);
    }

    [Fact]
    public void Methods_are_called_by_either_spelling_with_positional_and_keyword_arguments()
    {
        using var session = Open(new Scope());

        Assert.Equal("'anything'", session.Run("Q().start_at('anything').label").Outputs[0].Text.Replace("\"", "'"));
        Assert.Equal("5", session.Run("Q().Count(start=2, stop=7)").Outputs[0].Text);
        Assert.Equal("2", session.Run("Q().Count(2)").Outputs[0].Text);
    }

    [Fact]
    public void A_task_is_awaited_and_its_result_converted()
    {
        using var session = Open(new Scope());

        Assert.Equal("7", session.Run("AddAsync(3, 4)").Outputs[0].Text);
    }

    [Fact]
    public void Text_types_cross_as_strings_and_are_parsed_back()
    {
        using var session = Open(new Scope());

        var id = session.Run("Store.NewId()").Outputs[0].Text;

        Assert.Equal("'id-1'", id.Replace("\"", "'"));
        Assert.Equal("'id-1'", session.Run("Store.Echo('id-1')").Outputs[0].Text.Replace("\"", "'"));

        // Text that is not an id binds to no overload, and the error says which argument and what it should have been.
        var malformed = session.Run("Store.Echo('not an id')");

        Assert.Equal("TypeError", malformed.ErrorName);
        Assert.Contains("is not a Ident", malformed.Traceback);
    }

    [Fact]
    public void Collections_and_dictionaries_convert_both_ways()
    {
        using var session = Open(new Scope());

        Assert.Equal("[1, 2, 3]", session.Run("Store.Numbers()").Outputs[0].Text);
        Assert.Equal("6", session.Run("Store.Sum([1, 2, 3])").Outputs[0].Text);
        Assert.Equal("{'a': 1}", session.Run("Store.Map()").Outputs[0].Text.Replace("\"", "'"));
        Assert.Equal("2", session.Run("len(Store.Numbers()[:2])").Outputs[0].Text);
        Assert.Equal("[2, 3]", session.Run("[n + 1 for n in Store.Numbers()[:2]]").Outputs[0].Text);
    }

    [Fact]
    public void The_policy_hides_what_it_does_not_name()
    {
        using var session = Open(new Scope());

        // A hidden member name, on any type.
        Assert.Equal("AttributeError", session.Run("Store.Internals").ErrorName);

        // Object's and the framework's members are not declared by an exposed type, so GetType() and friends are not there.
        Assert.Equal("AttributeError", session.Run("Store.GetType()").ErrorName);
        Assert.Equal("AttributeError", session.Run("Store.GetHashCode()").ErrorName);

        // A value of a type the policy does not expose arrives as its text, not as an object to reach into.
        var outsider = session.Run("Store.Outsider()");

        Assert.True(outsider.Succeeded);
        Assert.Contains("abc", outsider.Outputs[0].Text);
        Assert.Equal("AttributeError", session.Run("Store.Outsider().Length").ErrorName);
    }

    [Fact]
    public void Members_the_host_marks_are_hidden_from_a_restricted_caller_and_from_completion()
    {
        var restricted = new HostObjectPolicy(
            t => t.Assembly == typeof(HostObjectTests).Assembly,
            hideMember: m => m.IsDefined(typeof(WriteAttribute), inherit: true));

        var library = PythonLibrary.FromSource("host", "Store = _scope.Store\n", _ => new Dictionary<string, PyObject> { ["_scope"] = PyHostObject.Wrap(new Scope(), restricted) });
        using var session = new PythonOptions { Libraries = [library] }.StartSession(new InMemoryFileSystem(), "/");

        session.Initialize("from host import *");

        Assert.Equal("[1, 2, 3]", session.Run("Store.Numbers()").Outputs[0].Text);
        Assert.Equal("AttributeError", session.Run("Store.Delete()").ErrorName);
        Assert.DoesNotContain("Delete", session.Complete("Store.", 6).Items);
        Assert.Contains("Numbers", session.Complete("Store.", 6).Items);
    }

    [Fact]
    public void Failures_become_python_exceptions_and_overload_errors_say_what_was_tried()
    {
        using var session = Open(new Scope());

        Assert.Equal("RuntimeError", session.Run("Store.Boom()").ErrorName);
        Assert.Equal("TypeError", session.Run("Store.Sum('nope')").ErrorName);
        Assert.Contains("Sum(", session.Run("Store.Sum('nope')").Traceback);
    }

    [Fact]
    public void A_cancellation_stays_a_cancellation_and_cannot_be_caught_by_the_program()
    {
        using var session = Open(new Scope());
        using var cancelled = new CancellationTokenSource();

        cancelled.Cancel();

        var result = session.Run("try:\n    Store.Cancelled()\nexcept Exception:\n    pass\n'swallowed'", cancelled.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("KeyboardInterrupt", result.ErrorName);
    }

    [Fact]
    public void Dir_and_completion_list_the_reachable_members()
    {
        using var session = Open(new Scope());

        Assert.Contains("NewId", session.Complete("Store.", 6).Items);
        Assert.DoesNotContain("Internals", session.Complete("Store.", 6).Items);
        Assert.DoesNotContain("GetType", session.Complete("Store.", 6).Items);
        Assert.Equal(["Echo"], session.Complete("Store.Ec", 8).Items);
        Assert.Contains("Q", session.Complete("Q", 1).Items);
    }

    [Fact]
    public void Completion_follows_a_call_chain_by_declared_types_without_calling_anything()
    {
        var scope = new Scope();

        using var session = Open(scope);

        // Q() returns a Query, StartAt returns a Query again, and Label is one of its members.
        Assert.Contains("StartAt", session.Complete("Q().", 4).Items);
        Assert.Contains("Label", session.Complete("Q().StartAt('x').", 17).Items);
        Assert.Equal(["Label"], session.Complete("Q().StartAt('x').Lab", 20).Items);

        // A member that returns a task completes with the awaited type's members.
        Assert.Contains("NewId", session.Complete("Store.Self().", 13).Items);

        // Properties are followed by their declared type; a Python call is not followed at all.
        Assert.Contains("Label", session.Complete("Store.Current.", 14).Items);
        session.Run("def f():\n    return Store");
        Assert.Empty(session.Complete("f().", 4).Items);

        Assert.Equal(0, scope.Calls);
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class WriteAttribute : Attribute;

public sealed class Ident(string value)
{
    public string Value { get; } = value;

    public static Ident Parse(string text) =>
        text.StartsWith("id-", StringComparison.Ordinal) ? new Ident(text) : throw new FormatException("not an id");

    public override string ToString() => Value;
}

public sealed class Query(string label)
{
    public string Label { get; } = label;

    public Query StartAt(string type) => new(type);

    public int Count(int stop = 3, int start = 0) => stop - start;
}

public sealed class Store
{
    private int _next;

    public Ident NewId() => new($"id-{++_next}");

    public string Echo(Ident id) => id.Value;

    public int[] Numbers() => [1, 2, 3];

    public int Sum(IEnumerable<int> numbers) => numbers.Sum();

    public Dictionary<string, int> Map() => new() { ["a"] = 1 };

    public object Outsider() => new System.Text.StringBuilder("abc");

    [Write]
    public int Delete() => 1;

    public string Boom() => throw new InvalidOperationException("boom");

    public string Cancelled() => throw new OperationCanceledException();

    public Task<Store> Self() => Task.FromResult(this);

    public Query Current => new("current");

    public object Internals => "hidden";
}

public sealed class Scope
{
    public int Calls { get; private set; }

    public Store Store { get; } = new();

    public Query Q() => new("anything-default");

    public Query Q(string label) => new(label);

    public Task<int> AddAsync(int a, int b) => Task.FromResult(a + b);

    public object Internals => "hidden";
}
