using System.Globalization;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Pandas;

/// <summary>
/// <c>DataFrame.query</c> and <c>eval</c> over a small, safe expression language: column
/// names (bare or in backticks), literals and lists, arithmetic, comparisons (chained),
/// <c>in</c> / <c>not in</c>, <c>and</c> / <c>or</c> / <c>not</c> and <c>&amp; | ~</c>, and
/// method calls on columns (<c>name.str.contains('x')</c>). It is parsed here, never handed
/// to the interpreter, so a query string can reach nothing a column cannot. <c>@variable</c>
/// is not supported: the query cannot see the caller's locals.
/// </summary>
internal static class PdQuery
{
    public static PdSeries Evaluate(PdFrame frame, string expression)
    {
        var result = EvaluateValue(frame, expression);
        return result as PdSeries ?? throw Pd.ValueError("query expression must evaluate to a boolean Series");
    }

    public static PyObject EvaluateValue(PdFrame frame, string expression)
    {
        var parser = new Parser(Lex(expression), frame);
        var value = parser.Expression();
        parser.End();
        return value;
    }

    private enum Kind
    {
        Name,
        Number,
        String,
        Op,
        End,
    }

    private readonly record struct Token(Kind Kind, string Text);

    private static List<Token> Lex(string source)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < source.Length)
        {
            var ch = source[i];

            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            if (char.IsAsciiDigit(ch) || (ch == '.' && i + 1 < source.Length && char.IsAsciiDigit(source[i + 1])))
            {
                var start = i;
                while (i < source.Length && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '.' || source[i] == '_'
                    || ((source[i] == '+' || source[i] == '-') && (source[i - 1] == 'e' || source[i - 1] == 'E'))))
                {
                    i++;
                }

                tokens.Add(new Token(Kind.Number, source[start..i].Replace("_", string.Empty, StringComparison.Ordinal)));
                continue;
            }

            if (char.IsLetter(ch) || ch == '_')
            {
                var start = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(Kind.Name, source[start..i]));
                continue;
            }

            if (ch == '`')
            {
                var end = source.IndexOf('`', i + 1);
                if (end < 0)
                {
                    throw Syntax("unterminated backtick");
                }

                tokens.Add(new Token(Kind.Name, source[(i + 1)..end]));
                i = end + 1;
                continue;
            }

            if (ch is '\'' or '"')
            {
                var builder = new StringBuilder();
                i++;

                while (i < source.Length && source[i] != ch)
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        i++;
                    }

                    builder.Append(source[i++]);
                }

                if (i >= source.Length)
                {
                    throw Syntax("unterminated string");
                }

                i++;
                tokens.Add(new Token(Kind.String, builder.ToString()));
                continue;
            }

            if (ch == '@')
            {
                throw new PyRaise(new PyException(PdModule.UndefinedVariableError,
                    "local variables ('@name') are not available to query() in this sandbox; format the value into the expression instead"));
            }

            var two = i + 1 < source.Length ? source.Substring(i, 2) : string.Empty;

            if (two is "==" or "!=" or "<=" or ">=" or "**" or "//")
            {
                tokens.Add(new Token(Kind.Op, two));
                i += 2;
                continue;
            }

            if ("<>+-*/%()[],.&|~=".Contains(ch, StringComparison.Ordinal))
            {
                tokens.Add(new Token(Kind.Op, ch.ToString()));
                i++;
                continue;
            }

            throw Syntax($"invalid character '{ch}'");
        }

        tokens.Add(new Token(Kind.End, string.Empty));
        return tokens;
    }

    private static PyRaise Syntax(string message) => Pd.ValueError($"invalid query expression: {message}");

    private sealed class Parser(List<Token> tokens, PdFrame frame)
    {
        private int _at;

        private Token Peek => tokens[_at];

        private bool Accept(string text)
        {
            if ((Peek.Kind is Kind.Op or Kind.Name) && Peek.Text == text)
            {
                _at++;
                return true;
            }

            return false;
        }

        private void Expect(string text)
        {
            if (!Accept(text))
            {
                throw Syntax($"expected '{text}'");
            }
        }

        public void End()
        {
            if (Peek.Kind != Kind.End)
            {
                throw Syntax($"unexpected '{Peek.Text}'");
            }
        }

        public PyObject Expression() => Or();

        private PyObject Or()
        {
            var left = And();
            while (Accept("or") || Accept("|"))
            {
                left = Operators.Binary("|", left, And());
            }

            return left;
        }

        private PyObject And()
        {
            var left = Not();
            while (Accept("and") || Accept("&"))
            {
                left = Operators.Binary("&", left, Not());
            }

            return left;
        }

        private PyObject Not()
        {
            if (Accept("not") || Accept("~"))
            {
                var operand = Not();
                return operand is PyBool flag ? PyBool.Of(!flag.Value) : Operators.Unary("~", operand);
            }

            return Comparison();
        }

        private PyObject Comparison()
        {
            var left = Sum();
            PyObject? result = null;

            while (true)
            {
                string? op = Peek.Text switch
                {
                    "==" or "!=" or "<" or "<=" or ">" or ">=" when Peek.Kind == Kind.Op => Peek.Text,
                    "in" when Peek.Kind == Kind.Name => "in",
                    "not" when Peek.Kind == Kind.Name && tokens[_at + 1].Text == "in" => "not in",
                    _ => null,
                };

                if (op is null)
                {
                    return result ?? left;
                }

                _at += op == "not in" ? 2 : 1;
                var right = Sum();
                var compared = op switch
                {
                    "in" => Membership(left, right, negate: false),
                    "not in" => Membership(left, right, negate: true),
                    _ => Operators.Compare(op, left, right),
                };

                result = result is null ? compared : Operators.Binary("&", result, compared);
                left = right;
            }
        }

        private static PyObject Membership(PyObject left, PyObject right, bool negate)
        {
            if (left is PdSeries series)
            {
                var values = right is PdSeries other ? new PyList([.. other.Data.Values]) : Pd.IsListLike(right) ? right : new PyList([right]);
                var mask = series.With(series.IsIn(values));
                return negate ? Operators.Unary("~", mask) : mask;
            }

            var contained = right.Contains(left);
            return PyBool.Of(negate ? !contained : contained);
        }

        private PyObject Sum()
        {
            var left = Term();
            while (true)
            {
                if (Accept("+"))
                {
                    left = Operators.Binary("+", left, Term());
                }
                else if (Accept("-"))
                {
                    left = Operators.Binary("-", left, Term());
                }
                else
                {
                    return left;
                }
            }
        }

        private PyObject Term()
        {
            var left = Unary();
            while (true)
            {
                var op = Peek.Kind == Kind.Op && Peek.Text is "*" or "/" or "//" or "%" ? Peek.Text : null;
                if (op is null)
                {
                    return left;
                }

                _at++;
                left = Operators.Binary(op, left, Unary());
            }
        }

        private PyObject Unary()
        {
            if (Accept("-"))
            {
                return Operators.Unary("-", Unary());
            }

            if (Accept("+"))
            {
                return Unary();
            }

            var value = Postfix();
            return Accept("**") ? Operators.Binary("**", value, Unary()) : value;
        }

        private PyObject Postfix()
        {
            var value = Atom();

            while (true)
            {
                if (Accept("."))
                {
                    var name = Peek.Kind == Kind.Name ? tokens[_at++].Text : throw Syntax("expected a name after '.'");

                    if (name.StartsWith('_'))
                    {
                        throw Syntax($"attribute '{name}' is not allowed in a query");
                    }

                    var runtime = PdRuntime.Current!;
                    var attribute = Attributes.Get(runtime.Machine, value, name);

                    if (Accept("("))
                    {
                        var arguments = new List<PyObject>();
                        var keywords = new PyDict();

                        while (!Accept(")"))
                        {
                            if (Peek.Kind == Kind.Name && tokens[_at + 1].Text == "=" )
                            {
                                var key = tokens[_at].Text;
                                _at += 2;
                                keywords.Set(new PyStr(key), Expression());
                            }
                            else
                            {
                                arguments.Add(Expression());
                            }

                            Accept(",");
                        }

                        value = runtime.Machine.Call(attribute, [.. arguments], keywords.Count > 0 ? keywords : null);
                    }
                    else
                    {
                        value = attribute;
                    }

                    continue;
                }

                if (Peek.Kind == Kind.Op && Peek.Text == "[" && value is PdSeries or PdFrame)
                {
                    _at++;
                    var key = Expression();
                    Expect("]");
                    value = value.GetItem(key);
                    continue;
                }

                return value;
            }
        }

        private PyObject Atom()
        {
            var token = tokens[_at++];

            switch (token.Kind)
            {
                case Kind.Number:
                    if (long.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var integer))
                    {
                        return PyInt.From(integer);
                    }

                    return double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        ? new PyFloat(number)
                        : throw Syntax($"invalid number '{token.Text}'");

                case Kind.String:
                    return new PyStr(token.Text);

                case Kind.Op when token.Text == "(":
                {
                    var inner = Expression();

                    if (Accept(","))
                    {
                        var items = new List<PyObject> { inner };
                        while (!Accept(")"))
                        {
                            items.Add(Expression());
                            Accept(",");
                        }

                        return new PyTuple(items);
                    }

                    Expect(")");
                    return inner;
                }

                case Kind.Op when token.Text == "[":
                {
                    var items = new List<PyObject>();
                    while (!Accept("]"))
                    {
                        items.Add(Expression());
                        Accept(",");
                    }

                    return new PyList(items);
                }

                case Kind.Name:
                    switch (token.Text)
                    {
                        case "True":
                            return PyBool.True;
                        case "False":
                            return PyBool.False;
                        case "None":
                            return PyNone.Instance;
                    }

                    if (frame.Columns.Find(new PyStr(token.Text)) is var at && at >= 0)
                    {
                        return frame.Column(at);
                    }

                    if (token.Text == "index" || (frame.Index.Name is PyStr name && name.Value == token.Text))
                    {
                        return new PdSeries(frame.Index.Labels, frame.Index, frame.Index.Name);
                    }

                    throw new PyRaise(new PyException(PdModule.UndefinedVariableError, $"name '{token.Text}' is not defined"));

                default:
                    throw Syntax($"unexpected '{token.Text}'");
            }
        }
    }
}
