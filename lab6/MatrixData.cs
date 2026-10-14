namespace Lab6;

internal static class MatrixData
{
    // Менша матриця БД2 обчислюється на host-комп'ютері.
    public static double[,] CreateBd2() => new double[,]
    {
        { -100,  50,  10,  25,  30 },
        {   50, -60,   5,  10,  25 },
        {   10,   5, -50,  20,  50 },
        {   25,  10,  20, -40,  10 },
        {   30,  25,  50,  10, -10 }
    };
}
