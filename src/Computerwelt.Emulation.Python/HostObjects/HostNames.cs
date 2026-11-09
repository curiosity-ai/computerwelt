using System.Text;

namespace Computerwelt.Emulation.Python.HostObjects;

/// <summary>How a host object's members are named to a program.</summary>
public enum HostNameStyle
{
    /// <summary><c>snake_case</c>: <c>StartAt</c> is <c>start_at</c>. What a program sees listed, completed and documented; the declared name is still accepted.</summary>
    Pythonic,

    /// <summary>The names as the host declared them.</summary>
    AsDeclared,
}

/// <summary>Name conversion between a host's C# naming and Python's.</summary>
public static class HostNames
{
    /// <summary>
    /// Converts a C# member or parameter name to <c>snake_case</c>: a lower-to-upper boundary starts a word, and so does the last capital of
    /// an acronym that is followed by a word — <c>AsUIDEnumerable</c> is <c>as_uid_enumerable</c>, <c>userUID</c> is <c>user_uid</c>, <c>Q</c> is <c>q</c>.
    /// </summary>
    public static string ToPython(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var result = new StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];

            if (char.IsUpper(c))
            {
                var startsWord = i > 0
                    && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])
                        || (char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1])));

                if (startsWord)
                {
                    result.Append('_');
                }

                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    /// <summary>A name with case and underscores ignored, so <c>start_at</c>, <c>StartAt</c> and <c>startat</c> are one.</summary>
    internal static string Key(string name) => name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
