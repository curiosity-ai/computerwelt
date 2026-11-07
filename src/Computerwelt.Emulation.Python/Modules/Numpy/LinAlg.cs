using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>
/// Products and <c>numpy.linalg</c>, in managed code.
/// </summary>
/// <remarks>
/// numpy calls LAPACK; this is textbook numerics (LU with partial pivoting, Gauss-Jordan,
/// Householder QR, cyclic Jacobi for symmetric eigenproblems). Results agree with numpy's
/// to rounding, not bit for bit, and an eigenvector's sign — which LAPACK leaves to its
/// implementation — may differ.
/// </remarks>
internal static class LinAlg
{
    /// <summary><c>numpy.linalg.LinAlgError</c>, a <c>ValueError</c>.</summary>
    public static PyExceptionType LinAlgError { get; } =
        PyExceptionType.DefineHostException("LinAlgError", PyExceptionType.ValueError);

    private static PyRaise Error(string message) => new(new PyException(LinAlgError, message));

    private static DKind ProductKind(NdArray a, NdArray b)
    {
        var kind = NdDType.Promote(a.Kind, b.Kind);

        return kind is DKind.Str or DKind.Object
            ? throw new PyRaise(PyErrors.TypeError("matrix products are only supported for numeric arrays here"))
            : kind;
    }

    /// <summary>(P, K) × (K, Q) row-major, in the result kind.</summary>
    private static NdArray Multiply(NdArray a, NdArray b, int p, int k, int q, DKind kind, int[] shape)
    {
        NdArray.CheckedSize(shape);
        NdArray.CheckWork((double)p * k * q, "this matrix product");

        if (kind == DKind.Float)
        {
            var x = a.ToDoubles();
            var y = b.ToDoubles();
            var result = new double[p * q];

            for (var i = 0; i < p; i++)
            {
                for (var j = 0; j < q; j++)
                {
                    var total = 0.0;

                    for (var m = 0; m < k; m++)
                    {
                        total += x[(i * k) + m] * y[(m * q) + j];
                    }

                    result[(i * q) + j] = total;
                }
            }

            return NdArray.FromDoubles(result, shape);
        }

        var xs = a.ToLongs();
        var ys = b.ToLongs();
        var longs = new long[p * q];

        unchecked
        {
            for (var i = 0; i < p; i++)
            {
                for (var j = 0; j < q; j++)
                {
                    long total = 0;

                    for (var m = 0; m < k; m++)
                    {
                        total += xs[(i * k) + m] * ys[(m * q) + j];
                    }

                    longs[(i * q) + j] = total;
                }
            }
        }

        var product = NdArray.FromLongs(longs, shape);
        return kind == DKind.Bool ? product.AsType(NdDType.Bool) : product;
    }

    /// <summary><c>a @ b</c>, with numpy's rules for vectors and stacks of matrices.</summary>
    public static PyObject MatMul(NdArray a, NdArray b)
    {
        for (var (operand, index) = (a, 0); index < 2; (operand, index) = (b, index + 1))
        {
            if (operand.NDim == 0)
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"matmul: Input operand {index} does not have enough dimensions (has 0, gufunc core with "
                    + "signature (n?,k),(k,m?)->(n?,m?) requires 1)"));
            }
        }

        var kind = ProductKind(a, b);
        var left = a.NDim == 1 ? a.Reshape([1, a.Shape[0]]) : a;
        var right = b.NDim == 1 ? b.Reshape([b.Shape[0], 1]) : b;
        var (n, k) = (left.Shape[^2], left.Shape[^1]);
        var (k2, m) = (right.Shape[^2], right.Shape[^1]);

        if (k != k2)
        {
            throw new PyRaise(PyErrors.ValueError(
                "matmul: Input operand 1 has a mismatch in its core dimension 0, with gufunc signature "
                + $"(n?,k),(k,m?)->(n?,m?) (size {k2} is different from {k})"));
        }

        var batch = NdArray.BroadcastShapes(left.Shape[..^2], right.Shape[..^2]);
        var leftAll = left.BroadcastTo([.. batch, n, k]).Contiguous();
        var rightAll = right.BroadcastTo([.. batch, k, m]).Contiguous();
        var batches = batch.Aggregate(1, static (x, y) => x * y);
        var outShape = new List<int>(batch);

        if (a.NDim > 1)
        {
            outShape.Add(n);
        }

        if (b.NDim > 1)
        {
            outShape.Add(m);
        }

        NdArray.CheckedSize([batches, n, m]);
        NdArray.CheckWork((double)batches * n * k * m, "this matrix product");
        var result = NdArray.Zeros([batches, n, m], NdDType.Of(kind == DKind.Bool ? DKind.Bool : kind));

        for (var i = 0; i < batches; i++)
        {
            var x = leftAll.Reshape([batches, n, k]).GetItem(PyInt.From(i)) as NdArray ?? throw new InvalidOperationException();
            var y = rightAll.Reshape([batches, k, m]).GetItem(PyInt.From(i)) as NdArray ?? throw new InvalidOperationException();
            var product = Multiply(x, y, n, k, m, kind, [n, m]);
            var slot = result.View([n, m], [m, 1], i * n * m);
            slot.Assign(slot.Offsets(), [n, m], product);
        }

        return NdConvert.Wrap(result.Reshape([.. outShape]));
    }

    /// <summary><c>np.dot</c>: a sum product over a's last axis and b's second-to-last.</summary>
    public static PyObject Dot(NdArray a, NdArray b)
    {
        if (a.NDim == 0 || b.NDim == 0)
        {
            return Ufuncs.Binary("multiply", a, b);
        }

        var kind = ProductKind(a, b);
        var k = a.Shape[^1];
        var bAxis = b.NDim == 1 ? 0 : b.NDim - 2;

        if (b.Shape[bAxis] != k)
        {
            throw new PyRaise(PyErrors.ValueError(
                $"shapes {NdArray.ShapeText(a.Shape)} and {NdArray.ShapeText(b.Shape)} not aligned: "
                + $"{k} (dim {a.NDim - 1}) != {b.Shape[bAxis]} (dim {bAxis})"));
        }

        var p = a.Shape[..^1].Aggregate(1, static (x, y) => x * y);
        var otherAxes = Enumerable.Range(0, b.NDim).Where(d => d != bAxis).ToArray();
        var q = otherAxes.Aggregate(1, (size, d) => size * b.Shape[d]);
        var bMoved = b.Transpose([bAxis, .. otherAxes]).Contiguous();
        int[] shape = [.. a.Shape[..^1], .. otherAxes.Select(d => b.Shape[d])];
        var product = Multiply(a.Contiguous(), bMoved, p, k, q, kind, shape);
        return NdConvert.Wrap(product);
    }

    /// <summary><c>np.inner</c>: a sum product over both last axes.</summary>
    public static PyObject Inner(NdArray a, NdArray b)
    {
        if (a.NDim == 0 || b.NDim == 0)
        {
            return Ufuncs.Binary("multiply", a, b);
        }

        return Dot(a, b.NDim == 1 ? b : b.SwapAxes(-1, -2));
    }

    /// <summary>A square float matrix as rows, refusing anything else.</summary>
    private static double[,] Square(PyObject value, string function)
    {
        var a = NdConvert.ToArray(value);

        if (a.NDim != 2)
        {
            throw Error($"{a.NDim}-dimensional array given. Array must be at least two-dimensional");
        }

        if (a.Shape[0] != a.Shape[1])
        {
            throw Error("Last 2 dimensions of the array must be square");
        }

        // Jacobi sweeps cost far more than one elimination, so eigh is charged accordingly.
        NdArray.CheckWork(Math.Pow(a.Shape[0], 3) * (function == "eigh" ? 60 : 4), function);
        return Matrix(a);
    }

    private static double[,] Matrix(NdArray a)
    {
        var values = a.ToDoubles();
        var (rows, columns) = (a.Shape[0], a.Shape[1]);
        var matrix = new double[rows, columns];

        for (var i = 0; i < rows; i++)
        {
            for (var j = 0; j < columns; j++)
            {
                matrix[i, j] = values[(i * columns) + j];
            }
        }

        return matrix;
    }

    private static NdArray FromMatrix(double[,] matrix)
    {
        var (rows, columns) = (matrix.GetLength(0), matrix.GetLength(1));
        var values = new double[rows * columns];

        for (var i = 0; i < rows; i++)
        {
            for (var j = 0; j < columns; j++)
            {
                values[(i * columns) + j] = matrix[i, j];
            }
        }

        return NdArray.FromDoubles(values, [rows, columns]);
    }

    /// <summary>LU decomposition with partial pivoting, in place. Returns the row swaps' sign, or 0 when singular.</summary>
    private static int Decompose(double[,] lu, int[] pivots)
    {
        var n = lu.GetLength(0);
        var sign = 1;

        for (var i = 0; i < n; i++)
        {
            pivots[i] = i;
        }

        for (var column = 0; column < n; column++)
        {
            var best = column;

            for (var row = column + 1; row < n; row++)
            {
                if (Math.Abs(lu[row, column]) > Math.Abs(lu[best, column]))
                {
                    best = row;
                }
            }

            if (lu[best, column] == 0)
            {
                return 0;
            }

            if (best != column)
            {
                for (var j = 0; j < n; j++)
                {
                    (lu[best, j], lu[column, j]) = (lu[column, j], lu[best, j]);
                }

                (pivots[best], pivots[column]) = (pivots[column], pivots[best]);
                sign = -sign;
            }

            for (var row = column + 1; row < n; row++)
            {
                var factor = lu[row, column] / lu[column, column];
                lu[row, column] = factor;

                for (var j = column + 1; j < n; j++)
                {
                    lu[row, j] -= factor * lu[column, j];
                }
            }
        }

        return sign;
    }

    public static PyObject Det(PyObject value)
    {
        var lu = Square(value, "det");
        var n = lu.GetLength(0);
        var sign = Decompose(lu, new int[n]);

        if (sign == 0)
        {
            return new PyFloat(0.0);
        }

        // numpy computes the determinant from slogdet — sign times exp of the summed log
        // magnitudes — so the last digit follows that route rather than a running product.
        var logDet = 0.0;

        for (var i = 0; i < n; i++)
        {
            if (lu[i, i] < 0)
            {
                sign = -sign;
            }

            logDet += Math.Log(Math.Abs(lu[i, i]));
        }

        return new PyFloat(sign * Math.Exp(logDet));
    }

    /// <summary>Solves A x = B for each column of B.</summary>
    private static double[,] SolveMatrix(double[,] a, double[,] b)
    {
        var n = a.GetLength(0);
        var lu = (double[,])a.Clone();
        var pivots = new int[n];

        if (Decompose(lu, pivots) == 0)
        {
            throw Error("Singular matrix");
        }

        var columns = b.GetLength(1);
        var x = new double[n, columns];

        for (var c = 0; c < columns; c++)
        {
            var y = new double[n];

            for (var i = 0; i < n; i++)
            {
                var total = b[pivots[i], c];

                for (var j = 0; j < i; j++)
                {
                    total -= lu[i, j] * y[j];
                }

                y[i] = total;
            }

            for (var i = n - 1; i >= 0; i--)
            {
                var total = y[i];

                for (var j = i + 1; j < n; j++)
                {
                    total -= lu[i, j] * x[j, c];
                }

                x[i, c] = total / lu[i, i];
            }
        }

        return x;
    }

    public static PyObject Inv(PyObject value)
    {
        var a = Square(value, "inv");
        var n = a.GetLength(0);
        var identity = new double[n, n];

        for (var i = 0; i < n; i++)
        {
            identity[i, i] = 1;
        }

        return FromMatrix(SolveMatrix(a, identity));
    }

    public static PyObject Solve(PyObject left, PyObject right)
    {
        var a = Square(left, "solve");
        var b = NdConvert.ToArray(right);
        var n = a.GetLength(0);

        if (b.Shape.Length == 0 || b.Shape[0] != n)
        {
            throw new PyRaise(PyErrors.ValueError(
                $"solve: Input operand 1 has a mismatch in its core dimension 0, with gufunc signature (m,m),(m,n)->(m,n) "
                + $"(size {(b.NDim == 0 ? 0 : b.Shape[0])} is different from {n})"));
        }

        var vector = b.NDim == 1;
        var x = SolveMatrix(a, Matrix(vector ? b.Reshape([n, 1]) : b));
        var result = FromMatrix(x);
        return vector ? result.Reshape([n]) : result;
    }

    /// <summary>Cyclic Jacobi: eigenvalues ascending, eigenvectors as columns.</summary>
    public static (double[] Values, double[,] Vectors) SymmetricEigen(double[,] matrix)
    {
        var n = matrix.GetLength(0);
        var a = (double[,])matrix.Clone();
        var v = new double[n, n];

        for (var i = 0; i < n; i++)
        {
            v[i, i] = 1;
        }

        for (var sweep = 0; sweep < 100; sweep++)
        {
            var off = 0.0;

            for (var p = 0; p < n; p++)
            {
                for (var q = p + 1; q < n; q++)
                {
                    off += a[p, q] * a[p, q];
                }
            }

            if (off < 1e-300)
            {
                break;
            }

            for (var p = 0; p < n; p++)
            {
                for (var q = p + 1; q < n; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-300)
                    {
                        continue;
                    }

                    var theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    var t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt((theta * theta) + 1));
                    var c = 1 / Math.Sqrt((t * t) + 1);
                    var s = t * c;

                    for (var k = 0; k < n; k++)
                    {
                        var (akp, akq) = (a[k, p], a[k, q]);
                        a[k, p] = (c * akp) - (s * akq);
                        a[k, q] = (s * akp) + (c * akq);
                    }

                    for (var k = 0; k < n; k++)
                    {
                        var (apk, aqk) = (a[p, k], a[q, k]);
                        a[p, k] = (c * apk) - (s * aqk);
                        a[q, k] = (s * apk) + (c * aqk);
                    }

                    for (var k = 0; k < n; k++)
                    {
                        var (vkp, vkq) = (v[k, p], v[k, q]);
                        v[k, p] = (c * vkp) - (s * vkq);
                        v[k, q] = (s * vkp) + (c * vkq);
                    }
                }
            }
        }

        var order = Enumerable.Range(0, n).OrderBy(i => a[i, i]).ToArray();
        var values = order.Select(i => a[i, i]).ToArray();
        var vectors = new double[n, n];

        for (var j = 0; j < n; j++)
        {
            // A sign convention, since the method fixes none: the largest component positive.
            var column = order[j];
            var largest = 0;

            for (var i = 1; i < n; i++)
            {
                if (Math.Abs(v[i, column]) > Math.Abs(v[largest, column]) + 1e-12)
                {
                    largest = i;
                }
            }

            var flip = v[largest, column] < 0 ? -1 : 1;

            for (var i = 0; i < n; i++)
            {
                vectors[i, j] = flip * v[i, column];
            }
        }

        return (values, vectors);
    }

    public static PyObject Eigh(PyObject value, bool vectors)
    {
        var a = Square(value, "eigh");
        var n = a.GetLength(0);

        // numpy reads only the lower triangle (UPLO='L'); mirror it.
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                a[i, j] = a[j, i];
            }
        }

        var (w, v) = SymmetricEigen(a);
        return vectors ? new PyTuple([NdArray.Vector(w), FromMatrix(v)]) : NdArray.Vector(w);
    }

    /// <summary>Singular values, from the eigenvalues of AᵀA, largest first.</summary>
    public static double[] SingularValues(NdArray a)
    {
        var (r, c) = (a.Shape[0], a.Shape[1]);
        var minor = (double)Math.Min(r, c);
        NdArray.CheckWork((minor * minor * Math.Max(r, c)) + (minor * minor * minor * 60), "singular values");
        var m = Matrix(a);
        var (rows, columns) = (m.GetLength(0), m.GetLength(1));
        var small = Math.Min(rows, columns);
        var gram = new double[small, small];

        for (var i = 0; i < small; i++)
        {
            for (var j = 0; j < small; j++)
            {
                var total = 0.0;

                if (rows >= columns)
                {
                    for (var k = 0; k < rows; k++)
                    {
                        total += m[k, i] * m[k, j];
                    }
                }
                else
                {
                    for (var k = 0; k < columns; k++)
                    {
                        total += m[i, k] * m[j, k];
                    }
                }

                gram[i, j] = total;
            }
        }

        return [.. SymmetricEigen(gram).Values.Select(static e => Math.Sqrt(Math.Max(e, 0))).OrderDescending()];
    }

    public static PyObject MatrixRank(PyObject value, PyObject? tolerance)
    {
        var a = NdConvert.ToArray(value);

        if (a.NDim < 2)
        {
            return PyInt.From(a.ToDoubles().Any(static v => v != 0) ? 1 : 0);
        }

        var s = SingularValues(a);

        if (s.Length == 0)
        {
            return PyInt.From(0);
        }

        var tol = tolerance is null or PyNone
            ? s[0] * Math.Max(a.Shape[0], a.Shape[1]) * 2.220446049250313e-16
            : NdArray.ToDouble(tolerance);

        // The singular values here come from AᵀA, which squares the conditioning; widen the
        // default tolerance to match what that costs in precision.
        if (tolerance is null or PyNone)
        {
            tol = Math.Max(tol, s[0] * 1e-10);
        }

        return PyInt.From(s.Count(v => v > tol));
    }

    public static PyObject Cholesky(PyObject value)
    {
        var a = Square(value, "cholesky");
        var n = a.GetLength(0);
        var l = new double[n, n];

        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j <= i; j++)
            {
                var total = a[i, j];

                for (var k = 0; k < j; k++)
                {
                    total -= l[i, k] * l[j, k];
                }

                if (i == j)
                {
                    if (total <= 0)
                    {
                        throw Error("Matrix is not positive definite");
                    }

                    l[i, i] = Math.Sqrt(total);
                }
                else
                {
                    l[i, j] = total / l[j, j];
                }
            }
        }

        return FromMatrix(l);
    }

    /// <summary>Least squares by Householder QR: the x minimizing ‖Ax − b‖.</summary>
    public static double[,] LeastSquares(double[,] a, double[,] b)
    {
        var (m, n) = (a.GetLength(0), a.GetLength(1));
        NdArray.CheckWork((double)m * n * (n + b.GetLength(1)) * 2, "least squares");
        var columns = b.GetLength(1);
        var r = (double[,])a.Clone();
        var y = (double[,])b.Clone();
        var steps = Math.Min(m, n);

        for (var k = 0; k < steps; k++)
        {
            var norm = 0.0;

            for (var i = k; i < m; i++)
            {
                norm += r[i, k] * r[i, k];
            }

            norm = Math.Sqrt(norm);

            if (norm == 0)
            {
                continue;
            }

            var alpha = r[k, k] > 0 ? -norm : norm;
            var v = new double[m];
            v[k] = r[k, k] - alpha;

            for (var i = k + 1; i < m; i++)
            {
                v[i] = r[i, k];
            }

            var vv = 0.0;

            for (var i = k; i < m; i++)
            {
                vv += v[i] * v[i];
            }

            if (vv == 0)
            {
                continue;
            }

            for (var j = k; j < n; j++)
            {
                var dot = 0.0;

                for (var i = k; i < m; i++)
                {
                    dot += v[i] * r[i, j];
                }

                var f = 2 * dot / vv;

                for (var i = k; i < m; i++)
                {
                    r[i, j] -= f * v[i];
                }
            }

            for (var j = 0; j < columns; j++)
            {
                var dot = 0.0;

                for (var i = k; i < m; i++)
                {
                    dot += v[i] * y[i, j];
                }

                var f = 2 * dot / vv;

                for (var i = k; i < m; i++)
                {
                    y[i, j] -= f * v[i];
                }
            }
        }

        var x = new double[n, columns];

        for (var c = 0; c < columns; c++)
        {
            for (var i = Math.Min(n, m) - 1; i >= 0; i--)
            {
                var total = y[i, c];

                for (var j = i + 1; j < n; j++)
                {
                    total -= r[i, j] * x[j, c];
                }

                x[i, c] = Math.Abs(r[i, i]) < 1e-300 ? 0 : total / r[i, i];
            }
        }

        return x;
    }

    public static PyObject Lstsq(PyObject left, PyObject right)
    {
        var a = NdConvert.ToArray(left);
        var b = NdConvert.ToArray(right);

        if (a.NDim != 2)
        {
            throw Error($"{a.NDim}-dimensional array given. Array must be two-dimensional");
        }

        var vector = b.NDim == 1;

        if (b.NDim is not (1 or 2) || b.Shape[0] != a.Shape[0])
        {
            throw Error("Incompatible dimensions");
        }

        var bm = Matrix(vector ? b.Reshape([b.Shape[0], 1]) : b);
        var am = Matrix(a);
        var x = LeastSquares(am, bm);
        var (m, n) = (a.Shape[0], a.Shape[1]);
        var s = SingularValues(a);
        var rank = (int)((PyInt)MatrixRank(a, null)).Value;
        var columns = bm.GetLength(1);
        var residuals = new List<double>();

        if (rank == n && m > n)
        {
            for (var c = 0; c < columns; c++)
            {
                var total = 0.0;

                for (var i = 0; i < m; i++)
                {
                    var fitted = 0.0;

                    for (var j = 0; j < n; j++)
                    {
                        fitted += am[i, j] * x[j, c];
                    }

                    total += (bm[i, c] - fitted) * (bm[i, c] - fitted);
                }

                residuals.Add(total);
            }
        }

        var solution = FromMatrix(x);
        return new PyTuple([
            vector ? solution.Reshape([n]) : solution,
            NdArray.Vector([.. residuals]),
            PyInt.From(rank),
            NdArray.Vector(s),
        ]);
    }

    public static PyObject Norm(PyObject value, PyObject? ord, PyObject? axis, bool keepDims)
    {
        var x = NdConvert.ToArray(value).AsType(NdDType.Float64);
        var axes = NdConvert.ToAxes(axis, x.NDim);

        if (axes is null && ord is null or PyNone)
        {
            var values = x.ToDoubles();
            var total = 0.0;

            foreach (var v in values)
            {
                total += v * v;
            }

            var result = Math.Sqrt(total);
            return keepDims ? NdArray.FromDoubles([result], [.. Enumerable.Repeat(1, x.NDim)]) : new PyFloat(result);
        }

        axes ??= [.. Enumerable.Range(0, x.NDim)];

        if (axes.Length == 1)
        {
            var p = ord is null or PyNone ? 2.0 : NdArray.ToDouble(ord);
            return Reductions.MapFloat(x, axes, keepDims, (row, _) => VectorNorm(row, p));
        }

        if (axes.Length == 2 && x.NDim == 2)
        {
            var m = Matrix(axes[0] < axes[1] ? x : x.Transpose());
            var (rows, columns) = (m.GetLength(0), m.GetLength(1));
            var name = ord is PyStr text ? text.Value : null;
            var p = name is null && ord is not (null or PyNone) ? NdArray.ToDouble(ord) : double.NaN;
            double result;

            if (name is "fro" || ord is null or PyNone)
            {
                result = Math.Sqrt(x.ToDoubles().Sum(static v => v * v));
            }
            else if (name is "nuc")
            {
                result = SingularValues(x).Sum();
            }
            else if (p is 1 or -1)
            {
                var sums = Enumerable.Range(0, columns).Select(j => Enumerable.Range(0, rows).Sum(i => Math.Abs(m[i, j])));
                result = p > 0 ? sums.Max() : sums.Min();
            }
            else if (double.IsInfinity(p))
            {
                var sums = Enumerable.Range(0, rows).Select(i => Enumerable.Range(0, columns).Sum(j => Math.Abs(m[i, j])));
                result = p > 0 ? sums.Max() : sums.Min();
            }
            else if (p is 2 or -2)
            {
                var s = SingularValues(x);
                result = p > 0 ? s[0] : s[^1];
            }
            else
            {
                throw new PyRaise(PyErrors.ValueError("Invalid norm order for matrices."));
            }

            return keepDims ? NdArray.FromDoubles([result], [1, 1]) : new PyFloat(result);
        }

        throw new PyRaise(PyErrors.ValueError("Improper number of dimensions to norm."));
    }

    private static double VectorNorm(double[] row, double p)
    {
        if (double.IsPositiveInfinity(p))
        {
            return row.Length == 0 ? 0 : row.Max(Math.Abs);
        }

        if (double.IsNegativeInfinity(p))
        {
            return row.Length == 0 ? 0 : row.Min(Math.Abs);
        }

        if (p == 0)
        {
            return row.Count(static v => v != 0);
        }

        if (p == 1)
        {
            return row.Sum(Math.Abs);
        }

        if (p == 2)
        {
            return Math.Sqrt(row.Sum(static v => v * v));
        }

        return Math.Pow(row.Sum(v => Math.Pow(Math.Abs(v), p)), 1 / p);
    }

    /// <summary><c>np.cross</c> for 3-vectors (and 2-vectors, giving the z component).</summary>
    public static PyObject Cross(NdArray a, NdArray b)
    {
        var x = a.AsType(NdDType.Float64);
        var y = b.AsType(NdDType.Float64);
        var (n, m) = (x.Shape[^1], y.Shape[^1]);

        if (n is not (2 or 3) || m is not (2 or 3))
        {
            throw new PyRaise(PyErrors.ValueError("incompatible dimensions for cross product\n(dimension must be 2 or 3)"));
        }

        var batch = NdArray.BroadcastShapes(x.Shape[..^1], y.Shape[..^1]);
        var xs = x.BroadcastTo([.. batch, n]).ToDoubles();
        var ys = y.BroadcastTo([.. batch, m]).ToDoubles();
        var count = batch.Aggregate(1, static (p, q) => p * q);
        var both2 = n == 2 && m == 2;
        var result = new double[count * (both2 ? 1 : 3)];

        for (var i = 0; i < count; i++)
        {
            var (a0, a1, a2) = (xs[i * n], xs[(i * n) + 1], n == 3 ? xs[(i * n) + 2] : 0);
            var (b0, b1, b2) = (ys[i * m], ys[(i * m) + 1], m == 3 ? ys[(i * m) + 2] : 0);

            if (both2)
            {
                result[i] = (a0 * b1) - (a1 * b0);
            }
            else
            {
                result[i * 3] = (a1 * b2) - (a2 * b1);
                result[(i * 3) + 1] = (a2 * b0) - (a0 * b2);
                result[(i * 3) + 2] = (a0 * b1) - (a1 * b0);
            }
        }

        var shape = both2 ? batch : [.. batch, 3];
        var product = NdArray.FromDoubles(result, shape);

        // Integer inputs give integer results, as numpy's do.
        return NdConvert.Wrap(a.Kind is DKind.Int or DKind.Bool && b.Kind is DKind.Int or DKind.Bool
            ? product.AsType(NdDType.Int64)
            : product);
    }
}
