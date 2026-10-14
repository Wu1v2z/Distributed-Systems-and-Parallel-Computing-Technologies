namespace Lab5;

internal static class EigenvalueSolver
{
    public static EigenResult FindLargestAlgebraic(
        double[,] matrix,
        double tolerance,
        int maximumIterations)
    {
        ValidateMatrix(matrix);

        int size = matrix.GetLength(0);
        double lowerGershgorinBound = CalculateLowerGershgorinBound(matrix);

        // Зсув робить усі власні числа B = A + shift*I додатними.
        // Тому найбільше алгебраїчне власне число A стає домінуючим для B.
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
}

internal sealed record EigenResult(
    double Eigenvalue,
    double ShiftedEigenvalue,
    double[] Eigenvector,
    double ResidualNorm,
    int Iterations,
    double LowerGershgorinBound,
    double Shift,
    bool Converged);
