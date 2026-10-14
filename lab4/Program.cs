using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Lab4;

internal static class Program
{
    private const double DefaultTolerance = 1e-12;
    private const int DefaultMaximumIterations = 10_000;

    // Матриця БД2 зі спільного завдання для лабораторних робіт 4–6.
    private static readonly double[,] Matrix =
    {
        { -100,  50,  10,  25,  30 },
        {   50, -60,   5,  10,  25 },
        {   10,   5, -50,  20,  50 },
        {   25,  10,  20, -40,  10 },
        {   30,  25,  50,  10, -10 }
    };

    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            ProgramConfiguration configuration = ParseArguments(args);
            if (configuration.ShowHelp)
            {
                PrintHelp();
                return;
            }

            ValidateMatrix(Matrix);

            Stopwatch timer = Stopwatch.StartNew();
            EigenResult result = FindLargestAlgebraicEigenvalue(
                Matrix,
                configuration.Tolerance,
                configuration.MaximumIterations);
            timer.Stop();

            PrintReport(Matrix, configuration, result, timer.Elapsed);

            if (!result.Converged)
            {
                Environment.ExitCode = 1;
            }
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Помилка: {exception.Message}");
            Console.Error.WriteLine("Використайте --help для перегляду параметрів.");
            Environment.ExitCode = 2;
        }
    }

    private static EigenResult FindLargestAlgebraicEigenvalue(
        double[,] matrix,
        double tolerance,
        int maximumIterations)
    {
        int size = matrix.GetLength(0);
        double lowerGershgorinBound = CalculateLowerGershgorinBound(matrix);

        // Після такого зсуву всі власні числа B = A + shift*I додатні.
        // Найбільше алгебраїчне власне число A стає найбільшим за модулем для B.
        double shift = lowerGershgorinBound < 0
            ? -lowerGershgorinBound + 1.0
            : 0.0;

        double[,] shiftedMatrix = AddDiagonalShift(matrix, shift);
        double[] vector = Enumerable.Repeat(1.0, size).ToArray();
        Normalize(vector);

        double previousEigenvalue = double.PositiveInfinity;
        double eigenvalue = double.NaN;
        double shiftedEigenvalue = double.NaN;
        double residualNorm = double.PositiveInfinity;
        double matrixNorm = CalculateFrobeniusNorm(matrix);

        for (int iteration = 1; iteration <= maximumIterations; iteration++)
        {
            double[] nextVector = Multiply(shiftedMatrix, vector);
            Normalize(nextVector);

            // Фіксуємо напрям знаку вектора, щоб він не змінювався між ітераціями.
            if (Dot(nextVector, vector) < 0)
            {
                MultiplyInPlace(nextVector, -1.0);
            }

            shiftedEigenvalue = RayleighQuotient(shiftedMatrix, nextVector);
            eigenvalue = shiftedEigenvalue - shift;
            residualNorm = CalculateResidualNorm(matrix, nextVector, eigenvalue);

            double eigenvalueChange = Math.Abs(eigenvalue - previousEigenvalue);
            double eigenvalueLimit = tolerance * Math.Max(1.0, Math.Abs(eigenvalue));
            double residualLimit = tolerance * Math.Max(1.0, matrixNorm) * 10.0;

            vector = nextVector;

            if (eigenvalueChange <= eigenvalueLimit && residualNorm <= residualLimit)
            {
                return new EigenResult(
                    eigenvalue,
                    shiftedEigenvalue,
                    vector,
                    residualNorm,
                    iteration,
                    lowerGershgorinBound,
                    shift,
                    Converged: true);
            }

            previousEigenvalue = eigenvalue;
        }

        return new EigenResult(
            eigenvalue,
            shiftedEigenvalue,
            vector,
            residualNorm,
            maximumIterations,
            lowerGershgorinBound,
            shift,
            Converged: false);
    }

    private static double CalculateLowerGershgorinBound(double[,] matrix)
    {
        int size = matrix.GetLength(0);
        double lowerBound = double.PositiveInfinity;

        for (int row = 0; row < size; row++)
        {
            double radius = 0;
            for (int column = 0; column < size; column++)
            {
                if (column != row)
                {
                    radius += Math.Abs(matrix[row, column]);
                }
            }

            lowerBound = Math.Min(lowerBound, matrix[row, row] - radius);
        }

        return lowerBound;
    }

    private static double[,] AddDiagonalShift(double[,] matrix, double shift)
    {
        int size = matrix.GetLength(0);
        double[,] result = (double[,])matrix.Clone();

        for (int index = 0; index < size; index++)
        {
            result[index, index] += shift;
        }

        return result;
    }

    private static double[] Multiply(double[,] matrix, double[] vector)
    {
        int rowCount = matrix.GetLength(0);
        int columnCount = matrix.GetLength(1);
        double[] result = new double[rowCount];

        for (int row = 0; row < rowCount; row++)
        {
            double sum = 0;
            for (int column = 0; column < columnCount; column++)
            {
                sum += matrix[row, column] * vector[column];
            }

            result[row] = sum;
        }

        return result;
    }

    private static double RayleighQuotient(double[,] matrix, double[] vector)
    {
        double[] multiplied = Multiply(matrix, vector);
        return Dot(vector, multiplied) / Dot(vector, vector);
    }

    private static double CalculateResidualNorm(
        double[,] matrix,
        double[] vector,
        double eigenvalue)
    {
        double[] multiplied = Multiply(matrix, vector);
        double sumOfSquares = 0;

        for (int index = 0; index < vector.Length; index++)
        {
            double difference = multiplied[index] - eigenvalue * vector[index];
            sumOfSquares += difference * difference;
        }

        return Math.Sqrt(sumOfSquares);
    }

    private static double CalculateFrobeniusNorm(double[,] matrix)
    {
        double sumOfSquares = 0;

        foreach (double value in matrix)
        {
            sumOfSquares += value * value;
        }

        return Math.Sqrt(sumOfSquares);
    }

    private static void Normalize(double[] vector)
    {
        double norm = Math.Sqrt(Dot(vector, vector));
        if (norm == 0 || !double.IsFinite(norm))
        {
            throw new ArgumentException("неможливо нормалізувати ітераційний вектор");
        }

        MultiplyInPlace(vector, 1.0 / norm);
    }

    private static double Dot(double[] left, double[] right)
    {
        double result = 0;
        for (int index = 0; index < left.Length; index++)
        {
            result += left[index] * right[index];
        }

        return result;
    }

    private static void MultiplyInPlace(double[] vector, double multiplier)
    {
        for (int index = 0; index < vector.Length; index++)
        {
            vector[index] *= multiplier;
        }
    }

    private static void ValidateMatrix(double[,] matrix)
    {
        int rowCount = matrix.GetLength(0);
        int columnCount = matrix.GetLength(1);

        if (rowCount != columnCount)
        {
            throw new ArgumentException("матриця має бути квадратною");
        }

        for (int row = 0; row < rowCount; row++)
        {
            for (int column = row + 1; column < columnCount; column++)
            {
                if (Math.Abs(matrix[row, column] - matrix[column, row]) > 1e-12)
                {
                    throw new ArgumentException(
                        "зміщений степеневий метод у цій реалізації очікує симетричну матрицю");
                }
            }
        }
    }

    private static void PrintReport(
        double[,] matrix,
        ProgramConfiguration configuration,
        EigenResult result,
        TimeSpan elapsed)
    {
        Console.WriteLine("Лабораторна робота №4 — найбільше власне число матриці 5×5");
        Console.WriteLine(new string('=', 72));
        Console.WriteLine("Матриця БД2:");
        PrintMatrix(matrix);
        Console.WriteLine();
        Console.WriteLine("Метод: зміщений степеневий метод із часткою Релея");
        Console.WriteLine($"Точність: {configuration.Tolerance:E3}");
        Console.WriteLine($"Нижня межа Гершгоріна: {result.LowerGershgorinBound:F6}");
        Console.WriteLine($"Діагональний зсув: {result.Shift:F6}");
        Console.WriteLine($"Найбільше власне число зміщеної матриці: {result.ShiftedEigenvalue:F12}");
        Console.WriteLine();
        Console.WriteLine($"L1 = {result.Eigenvalue:F12}");
        Console.WriteLine($"Власний вектор: ({string.Join("; ", result.Eigenvector.Select(value => value.ToString("F9")))})");
        Console.WriteLine($"Кількість ітерацій: {result.Iterations}");
        Console.WriteLine($"Норма нев’язки ||Av - L1*v||₂: {result.ResidualNorm:E6}");
        Console.WriteLine($"Час обчислення: {elapsed.TotalMilliseconds:F3} мс");
        Console.WriteLine($"Статус: {(result.Converged ? "ЗБІЖНІСТЬ ДОСЯГНУТО" : "ЛІМІТ ІТЕРАЦІЙ")}");
    }

    private static void PrintMatrix(double[,] matrix)
    {
        int rowCount = matrix.GetLength(0);
        int columnCount = matrix.GetLength(1);

        for (int row = 0; row < rowCount; row++)
        {
            Console.Write("  [");
            for (int column = 0; column < columnCount; column++)
            {
                Console.Write($"{matrix[row, column],9:F0}");
            }

            Console.WriteLine(" ]");
        }
    }

    private static ProgramConfiguration ParseArguments(string[] args)
    {
        double tolerance = DefaultTolerance;
        int maximumIterations = DefaultMaximumIterations;
        bool showHelp = false;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--tolerance":
                    tolerance = ParsePositiveDouble(
                        ReadNextValue(args, ref index),
                        "--tolerance");
                    break;
                case "--max-iterations":
                    maximumIterations = ParsePositiveInt(
                        ReadNextValue(args, ref index),
                        "--max-iterations");
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"невідомий параметр «{args[index]}»");
            }
        }

        return new ProgramConfiguration(tolerance, maximumIterations, showHelp);
    }

    private static string ReadNextValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"для параметра {args[index]} не вказано значення");
        }

        index++;
        return args[index];
    }

    private static double ParsePositiveDouble(string value, string parameterName)
    {
        bool parsed = double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double result);

        if (!parsed)
        {
            parsed = double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out result);
        }

        if (!parsed || result <= 0 || !double.IsFinite(result))
        {
            throw new ArgumentException(
                $"{parameterName} має бути додатним скінченним числом");
        }

        return result;
    }

    private static int ParsePositiveInt(string value, string parameterName)
    {
        if (!int.TryParse(value, out int result) || result <= 0)
        {
            throw new ArgumentException($"{parameterName} має бути додатним цілим числом");
        }

        return result;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Лабораторна робота №4");
        Console.WriteLine();
        Console.WriteLine("Параметри:");
        Console.WriteLine("  --tolerance VALUE       точність обчислення, типово 1e-12");
        Console.WriteLine("  --max-iterations N      максимальна кількість ітерацій, типово 10000");
        Console.WriteLine("  -h, --help              показати довідку");
    }

    private sealed record ProgramConfiguration(
        double Tolerance,
        int MaximumIterations,
        bool ShowHelp);

    private sealed record EigenResult(
        double Eigenvalue,
        double ShiftedEigenvalue,
        double[] Eigenvector,
        double ResidualNorm,
        int Iterations,
        double LowerGershgorinBound,
        double Shift,
        bool Converged);
}
