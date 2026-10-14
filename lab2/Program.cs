using System.Diagnostics;
using System.Text;

namespace Lab2;

internal static class Program
{
    private const int MaxValue = 100;
    private const int DefaultSubarrayCount = 4;
    private const int TargetElementOperationsPerArray = 50_000_000;

    private static readonly int[] DefaultArraySizes = [100, 1_000, 5_000, 10_000];

    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            LabConfiguration configuration = ParseArguments(args);
            if (configuration.ShowHelp)
            {
                PrintHelp();
                return;
            }

            ValidateConfiguration(configuration);

            using Process process = Process.GetCurrentProcess();

            // Одноразовий прогрів прибирає вплив JIT-компіляції з вимірювань.
            int warmUpSize = Math.Max(100, configuration.SubarrayCount);
            _ = RunForArray(warmUpSize, configuration.SubarrayCount, process, benchmarkPasses: 1);

            process.Refresh();
            TimeSpan totalCpuBefore = process.TotalProcessorTime;
            Stopwatch totalProgramTimer = Stopwatch.StartNew();

            List<ArrayRunResult> runResults = [];
            foreach (int arraySize in configuration.ArraySizes)
            {
                int benchmarkPasses = Math.Max(
                    1,
                    TargetElementOperationsPerArray / arraySize);

                runResults.Add(RunForArray(
                    arraySize,
                    configuration.SubarrayCount,
                    process,
                    benchmarkPasses));
            }

            totalProgramTimer.Stop();
            process.Refresh();
            TimeSpan totalCpuTime = process.TotalProcessorTime - totalCpuBefore;

            PrintReport(
                configuration,
                runResults,
                totalProgramTimer.Elapsed,
                totalCpuTime);

            if (runResults.Any(result => !result.IsCorrect))
            {
                Environment.ExitCode = 1;
            }
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Помилка параметрів: {exception.Message}");
            Console.Error.WriteLine("Використайте --help, щоб переглянути приклади запуску.");
            Environment.ExitCode = 2;
        }
    }

    private static ArrayRunResult RunForArray(
        int arraySize,
        int subarrayCount,
        Process process,
        int benchmarkPasses)
    {
        process.Refresh();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        Stopwatch arrayTimer = Stopwatch.StartNew();

        int[] numbers = CreateBoundedArray(arraySize, out long expectedSum);
        ArrayPart[] parts = SplitIntoParts(numbers.Length, subarrayCount);
        PartResult?[] partResults = new PartResult?[parts.Length];

        Stopwatch parallelTimer = Stopwatch.StartNew();
        CreateAndAwaitWorkersRecursively(
            numbers,
            parts,
            partResults,
            benchmarkPasses,
            partIndex: 0);
        parallelTimer.Stop();

        long totalSum = partResults.Sum(result => result!.Sum);

        arrayTimer.Stop();
        process.Refresh();
        TimeSpan cpuTime = process.TotalProcessorTime - cpuBefore;

        return new ArrayRunResult(
            arraySize,
            parts,
            partResults.Select(result => result!).ToArray(),
            totalSum,
            expectedSum,
            benchmarkPasses,
            parallelTimer.Elapsed,
            arrayTimer.Elapsed,
            cpuTime);
    }

    private static int[] CreateBoundedArray(int length, out long expectedSum)
    {
        int[] numbers = new int[length];
        long sum = 0;

        for (int index = 0; index < numbers.Length; index++)
        {
            int value = index % MaxValue + 1;
            numbers[index] = value;
            sum += value;
        }

        expectedSum = sum;
        return numbers;
    }

    private static ArrayPart[] SplitIntoParts(int arrayLength, int partCount)
    {
        ArrayPart[] parts = new ArrayPart[partCount];
        int baseLength = arrayLength / partCount;
        int remainder = arrayLength % partCount;
        int startIndex = 0;

        for (int partIndex = 0; partIndex < partCount; partIndex++)
        {
            // Перші remainder частин отримують по одному додатковому елементу.
            int currentLength = baseLength + (partIndex < remainder ? 1 : 0);
            parts[partIndex] = new ArrayPart(startIndex, currentLength);
            startIndex += currentLength;
        }

        return parts;
    }

    private static void CreateAndAwaitWorkersRecursively(
        int[] numbers,
        ArrayPart[] parts,
        PartResult?[] results,
        int benchmarkPasses,
        int partIndex)
    {
        // Базовий випадок: потоки для всіх підмасивів уже створено.
        if (partIndex >= parts.Length)
        {
            return;
        }

        int currentPartIndex = partIndex;
        Thread worker = new(() =>
            ProcessPart(
                numbers,
                parts[currentPartIndex],
                results,
                currentPartIndex,
                benchmarkPasses))
        {
            Name = $"Array-part-{currentPartIndex + 1}"
        };

        worker.Start();

        // Наступний потік створюється рекурсивно до виклику Join().
        // Отже, всі потоки запускаються до початку їх очікування.
        CreateAndAwaitWorkersRecursively(
            numbers,
            parts,
            results,
            benchmarkPasses,
            partIndex + 1);

        worker.Join();
    }

    private static void ProcessPart(
        int[] numbers,
        ArrayPart part,
        PartResult?[] results,
        int partIndex,
        int benchmarkPasses)
    {
        Stopwatch timer = Stopwatch.StartNew();
        long checksum = 0;

        int endIndex = part.StartIndex + part.Length;
        for (int pass = 0; pass < benchmarkPasses; pass++)
        {
            long passSum = 0;
            for (int index = part.StartIndex; index < endIndex; index++)
            {
                passSum += numbers[index];
            }

            checksum += passSum;
        }

        timer.Stop();

        // Кожний потік записує лише у власну комірку — блокування не потрібне.
        results[partIndex] = new PartResult(
            checksum / benchmarkPasses,
            Thread.CurrentThread.ManagedThreadId,
            timer.Elapsed);
    }

    private static LabConfiguration ParseArguments(string[] args)
    {
        int subarrayCount = DefaultSubarrayCount;
        int[] arraySizes = [.. DefaultArraySizes];
        bool showHelp = false;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--parts":
                case "-p":
                    subarrayCount = ParsePositiveInt(ReadNextValue(args, ref index), "--parts");
                    break;

                case "--sizes":
                case "-s":
                    arraySizes = ParseSizes(ReadNextValue(args, ref index));
                    break;

                case "--help":
                case "-h":
                    showHelp = true;
                    break;

                default:
                    throw new ArgumentException($"невідомий параметр «{args[index]}»");
            }
        }

        return new LabConfiguration(arraySizes, subarrayCount, showHelp);
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

    private static int ParsePositiveInt(string value, string parameterName)
    {
        if (!int.TryParse(value, out int parsedValue) || parsedValue <= 0)
        {
            throw new ArgumentException(
                $"{parameterName} має бути додатним цілим числом, отримано «{value}»");
        }

        return parsedValue;
    }

    private static int[] ParseSizes(string value)
    {
        string[] tokens = value.Split(
            [',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            throw new ArgumentException("список --sizes не може бути порожнім");
        }

        int[] sizes = new int[tokens.Length];
        for (int index = 0; index < tokens.Length; index++)
        {
            sizes[index] = ParsePositiveInt(tokens[index], "--sizes");
        }

        return sizes;
    }

    private static void ValidateConfiguration(LabConfiguration configuration)
    {
        int smallestArray = configuration.ArraySizes.Min();
        if (configuration.SubarrayCount > smallestArray)
        {
            throw new ArgumentException(
                $"кількість підмасивів ({configuration.SubarrayCount}) не може перевищувати " +
                $"розмір найменшого масиву ({smallestArray})");
        }
    }

    private static void PrintReport(
        LabConfiguration configuration,
        IReadOnlyList<ArrayRunResult> runResults,
        TimeSpan totalProgramTime,
        TimeSpan totalCpuTime)
    {
        Console.WriteLine("Лабораторна робота №2 — довільна кількість підмасивів");
        Console.WriteLine(new string('=', 65));
        Console.WriteLine($"Розміри масивів: {string.Join(", ", configuration.ArraySizes.Select(size => size.ToString("N0")))}");
        Console.WriteLine($"Кількість підмасивів для кожного масиву: {configuration.SubarrayCount}");
        Console.WriteLine($"Кількість логічних процесорів: {Environment.ProcessorCount}");
        Console.WriteLine(
            $"Ціль стабільного вимірювання: приблизно {TargetElementOperationsPerArray:N0} " +
            "операцій додавання на масив");

        foreach (ArrayRunResult runResult in runResults)
        {
            Console.WriteLine();
            Console.WriteLine($"Масив на {runResult.ArraySize:N0} елементів");
            Console.WriteLine(new string('-', 45));
            Console.WriteLine($"  Контрольних проходів: {runResult.BenchmarkPasses:N0}");

            for (int index = 0; index < runResult.Parts.Length; index++)
            {
                ArrayPart part = runResult.Parts[index];
                PartResult partResult = runResult.PartResults[index];
                int firstPosition = part.StartIndex + 1;
                int lastPosition = part.StartIndex + part.Length;

                Console.WriteLine(
                    $"  Частина {index + 1}: позиції {firstPosition:N0}..{lastPosition:N0}; " +
                    $"елементів {part.Length:N0}; потік {partResult.ManagedThreadId}; " +
                    $"сума {partResult.Sum:N0}; " +
                    $"середній час {partResult.Elapsed.TotalMicroseconds / runResult.BenchmarkPasses:F3} мкс");
            }

            Console.WriteLine($"  Загальна сума: {runResult.TotalSum:N0}");
            Console.WriteLine($"  Контрольна сума: {runResult.ExpectedSum:N0}");
            Console.WriteLine($"  Перевірка: {(runResult.IsCorrect ? "УСПІШНО" : "ПОМИЛКА")}");
            Console.WriteLine($"  Повний час серії: {runResult.ParallelTime.TotalMilliseconds:F3} мс");
            Console.WriteLine(
                $"  Середній час одного паралельного обчислення: " +
                $"{runResult.AverageParallelTimeMicroseconds:F3} мкс");
            Console.WriteLine($"  Повний час роботи з масивом: {runResult.TotalTime.TotalMilliseconds:F3} мс");
            Console.WriteLine($"  Процесорний час серії: {runResult.CpuTime.TotalMilliseconds:F3} мс");
        }

        Console.WriteLine();
        Console.WriteLine("Підсумкова таблиця");
        Console.WriteLine(new string('-', 96));
        Console.WriteLine(
            $"{"Розмір",12} {"Частин",9} {"Сума",16} {"Сер. час, мкс",17} " +
            $"{"Серія, мс",12} {"CPU, мс",11} {"Стан",10}");

        foreach (ArrayRunResult result in runResults)
        {
            Console.WriteLine(
                $"{result.ArraySize,12:N0} {result.Parts.Length,9} {result.TotalSum,16:N0} " +
                $"{result.AverageParallelTimeMicroseconds,17:F3} " +
                $"{result.ParallelTime.TotalMilliseconds,12:F3} " +
                $"{result.CpuTime.TotalMilliseconds,11:F3} " +
                $"{(result.IsCorrect ? "УСПІШНО" : "ПОМИЛКА"),10}");
        }

        Console.WriteLine(new string('-', 96));
        Console.WriteLine($"Загальний реальний час програми: {totalProgramTime.TotalMilliseconds:F3} мс");
        Console.WriteLine($"Сумарний процесорний час програми: {totalCpuTime.TotalMilliseconds:F3} мс");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Лабораторна робота №2");
        Console.WriteLine();
        Console.WriteLine("Параметри:");
        Console.WriteLine("  -p, --parts N       кількість підмасивів (типово 4)");
        Console.WriteLine("  -s, --sizes LIST    розміри масивів через кому");
        Console.WriteLine("  -h, --help          показати цю довідку");
        Console.WriteLine();
        Console.WriteLine("Приклади:");
        Console.WriteLine("  dotnet run --project .\\lab2\\Lab2.csproj");
        Console.WriteLine("  dotnet run --project .\\lab2\\Lab2.csproj -- --parts 7 --sizes 100,777,10000");
    }

    private readonly record struct ArrayPart(int StartIndex, int Length);

    private sealed record PartResult(long Sum, int ManagedThreadId, TimeSpan Elapsed);

    private sealed record ArrayRunResult(
        int ArraySize,
        ArrayPart[] Parts,
        PartResult[] PartResults,
        long TotalSum,
        long ExpectedSum,
        int BenchmarkPasses,
        TimeSpan ParallelTime,
        TimeSpan TotalTime,
        TimeSpan CpuTime)
    {
        public bool IsCorrect => TotalSum == ExpectedSum;

        public double AverageParallelTimeMicroseconds =>
            ParallelTime.TotalMicroseconds / BenchmarkPasses;
    }

    private sealed record LabConfiguration(
        int[] ArraySizes,
        int SubarrayCount,
        bool ShowHelp);
}
