using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Lab3;

internal static class Program
{
    private const int MaxValue = 100;
    private const int DefaultArraySize = 1_000_000;
    private const int DefaultMaxBlocks = 50;
    private const int DefaultRunCount = 5;
    private const int DefaultPassesPerRun = 20;
    private const int ChartWidth = 1_200;
    private const int ChartHeight = 820;

    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            BenchmarkConfiguration configuration = ParseArguments(args);
            if (configuration.ShowHelp)
            {
                PrintHelp();
                return;
            }

            ValidateConfiguration(configuration);

            int[] numbers = CreateBoundedArray(configuration.ArraySize, out long expectedSum);

            // Прогрів JIT-компілятора не входить до результатів експерименту.
            _ = ExecuteTrial(numbers, expectedSum, blockCount: 1, passesPerRun: 1);

            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            TimeSpan totalCpuBefore = process.TotalProcessorTime;
            Stopwatch totalBenchmarkTimer = Stopwatch.StartNew();

            List<DistributionResult> results = [];
            for (int blockCount = 1; blockCount <= configuration.MaxBlocks; blockCount++)
            {
                results.Add(BenchmarkDistribution(
                    numbers,
                    expectedSum,
                    blockCount,
                    configuration.RunCount,
                    configuration.PassesPerRun,
                    process));
            }

            totalBenchmarkTimer.Stop();
            process.Refresh();
            TimeSpan totalCpuTime = process.TotalProcessorTime - totalCpuBefore;

            AddSpeedupAndEfficiency(results);

            string outputDirectory = Path.GetFullPath(configuration.OutputDirectory);
            Directory.CreateDirectory(outputDirectory);

            string csvPath = Path.Combine(outputDirectory, "benchmark-results.csv");
            string svgPath = Path.Combine(outputDirectory, "performance-chart.svg");

            WriteCsv(csvPath, configuration, results);
            WriteSvgChart(svgPath, configuration, results);

            PrintReport(
                configuration,
                results,
                totalBenchmarkTimer.Elapsed,
                totalCpuTime,
                csvPath,
                svgPath);

            if (results.Any(result => !result.IsCorrect))
            {
                Environment.ExitCode = 1;
            }
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Помилка параметрів: {exception.Message}");
            Console.Error.WriteLine("Використайте --help, щоб переглянути доступні параметри.");
            Environment.ExitCode = 2;
        }
    }

    private static DistributionResult BenchmarkDistribution(
        int[] numbers,
        long expectedSum,
        int blockCount,
        int runCount,
        int passesPerRun,
        Process process)
    {
        process.Refresh();
        TimeSpan cpuBefore = process.TotalProcessorTime;

        TrialResult[] trials = new TrialResult[runCount];
        for (int runIndex = 0; runIndex < runCount; runIndex++)
        {
            trials[runIndex] = ExecuteTrial(
                numbers,
                expectedSum,
                blockCount,
                passesPerRun);
        }

        process.Refresh();
        TimeSpan totalCpuTime = process.TotalProcessorTime - cpuBefore;

        double averageElapsedMilliseconds = trials.Average(
            trial => trial.Elapsed.TotalMilliseconds);

        return new DistributionResult(
            blockCount,
            averageElapsedMilliseconds,
            totalCpuTime.TotalMilliseconds / runCount,
            trials.Min(trial => trial.Elapsed.TotalMilliseconds),
            trials.Max(trial => trial.Elapsed.TotalMilliseconds),
            trials.All(trial => trial.IsCorrect));
    }

    private static TrialResult ExecuteTrial(
        int[] numbers,
        long expectedSum,
        int blockCount,
        int passesPerRun)
    {
        ArrayPart[] parts = SplitIntoParts(numbers.Length, blockCount);
        PartResult?[] results = new PartResult?[parts.Length];

        Stopwatch timer = Stopwatch.StartNew();
        CreateAndAwaitWorkersRecursively(
            numbers,
            parts,
            results,
            passesPerRun,
            partIndex: 0);

        long totalSum = results.Sum(result => result!.Sum);
        timer.Stop();

        return new TrialResult(totalSum, expectedSum, timer.Elapsed);
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
        int passesPerRun,
        int partIndex)
    {
        if (partIndex >= parts.Length)
        {
            return;
        }

        int currentPartIndex = partIndex;
        Thread worker = new(() => ProcessPart(
            numbers,
            parts[currentPartIndex],
            results,
            currentPartIndex,
            passesPerRun))
        {
            Name = $"Block-{currentPartIndex + 1}"
        };

        worker.Start();

        // Потоки створюються рекурсивно до початку очікування через Join().
        CreateAndAwaitWorkersRecursively(
            numbers,
            parts,
            results,
            passesPerRun,
            partIndex + 1);

        worker.Join();
    }

    private static void ProcessPart(
        int[] numbers,
        ArrayPart part,
        PartResult?[] results,
        int partIndex,
        int passesPerRun)
    {
        long checksum = 0;
        int endIndex = part.StartIndex + part.Length;

        for (int pass = 0; pass < passesPerRun; pass++)
        {
            long passSum = 0;
            for (int index = part.StartIndex; index < endIndex; index++)
            {
                passSum += numbers[index];
            }

            checksum += passSum;
        }

        // Ділення повертає суму одного проходу після стабільного вимірювання.
        results[partIndex] = new PartResult(checksum / passesPerRun);
    }

    private static void AddSpeedupAndEfficiency(IReadOnlyList<DistributionResult> results)
    {
        double baselineTime = results[0].AverageElapsedMilliseconds;

        foreach (DistributionResult result in results)
        {
            result.Speedup = baselineTime / result.AverageElapsedMilliseconds;
            result.EfficiencyPercent = result.Speedup / result.BlockCount * 100.0;
        }
    }

    private static void WriteCsv(
        string path,
        BenchmarkConfiguration configuration,
        IReadOnlyList<DistributionResult> results)
    {
        StringBuilder csv = new();
        csv.AppendLine("BlockCount;AverageElapsedMs;AverageCpuMs;Speedup;EfficiencyPercent;MinElapsedMs;MaxElapsedMs;Correct");

        foreach (DistributionResult result in results)
        {
            csv.Append(result.BlockCount).Append(';')
                .Append(FormatInvariant(result.AverageElapsedMilliseconds)).Append(';')
                .Append(FormatInvariant(result.AverageCpuMilliseconds)).Append(';')
                .Append(FormatInvariant(result.Speedup)).Append(';')
                .Append(FormatInvariant(result.EfficiencyPercent)).Append(';')
                .Append(FormatInvariant(result.MinElapsedMilliseconds)).Append(';')
                .Append(FormatInvariant(result.MaxElapsedMilliseconds)).Append(';')
                .AppendLine(result.IsCorrect ? "true" : "false");
        }

        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteSvgChart(
        string path,
        BenchmarkConfiguration configuration,
        IReadOnlyList<DistributionResult> results)
    {
        const double left = 90;
        const double right = 40;
        const double plotWidth = ChartWidth - left - right;
        const double timeTop = 95;
        const double timeHeight = 280;
        const double efficiencyTop = 490;
        const double efficiencyHeight = 220;

        double maxTime = results.Max(result => result.AverageElapsedMilliseconds) * 1.1;
        double maxEfficiency = Math.Max(
            100,
            Math.Ceiling(results.Max(result => result.EfficiencyPercent) / 20.0) * 20.0);

        double X(int blockCount) =>
            left + (blockCount - 1) * plotWidth / Math.Max(1, results.Count - 1);

        double TimeY(double milliseconds) =>
            timeTop + timeHeight - milliseconds / maxTime * timeHeight;

        double EfficiencyY(double percent) =>
            efficiencyTop + efficiencyHeight - percent / maxEfficiency * efficiencyHeight;

        StringBuilder svg = new();
        svg.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{ChartWidth}\" height=\"{ChartHeight}\" viewBox=\"0 0 {ChartWidth} {ChartHeight}\">");
        svg.AppendLine("  <rect width=\"100%\" height=\"100%\" fill=\"#f8fafc\"/>");
        svg.AppendLine("  <style>text{font-family:Segoe UI,Arial,sans-serif;fill:#172033}.title{font-size:24px;font-weight:700}.subtitle{font-size:14px;fill:#536179}.axis{stroke:#758096;stroke-width:1}.grid{stroke:#d9dee8;stroke-width:1}.label{font-size:12px}.chart-title{font-size:17px;font-weight:600}</style>");
        svg.AppendLine("  <text x=\"600\" y=\"35\" text-anchor=\"middle\" class=\"title\">Лабораторна робота №3 — результати кластеризації</text>");
        svg.AppendLine($"  <text x=\"600\" y=\"60\" text-anchor=\"middle\" class=\"subtitle\">Масив: {configuration.ArraySize:N0}; запусків: {configuration.RunCount}; проходів у запуску: {configuration.PassesPerRun}</text>");

        AppendChartAxes(svg, left, plotWidth, timeTop, timeHeight, maxTime, "Середній реальний час, мс");
        AppendChartAxes(svg, left, plotWidth, efficiencyTop, efficiencyHeight, maxEfficiency, "Ефективність, %");

        string timePoints = string.Join(
            " ",
            results.Select(result =>
                $"{FormatInvariant(X(result.BlockCount))},{FormatInvariant(TimeY(result.AverageElapsedMilliseconds))}"));

        string efficiencyPoints = string.Join(
            " ",
            results.Select(result =>
                $"{FormatInvariant(X(result.BlockCount))},{FormatInvariant(EfficiencyY(result.EfficiencyPercent))}"));

        svg.AppendLine($"  <polyline points=\"{timePoints}\" fill=\"none\" stroke=\"#2563eb\" stroke-width=\"3\"/>");
        svg.AppendLine($"  <polyline points=\"{efficiencyPoints}\" fill=\"none\" stroke=\"#059669\" stroke-width=\"3\"/>");

        foreach (DistributionResult result in results)
        {
            double x = X(result.BlockCount);
            svg.AppendLine($"  <circle cx=\"{FormatInvariant(x)}\" cy=\"{FormatInvariant(TimeY(result.AverageElapsedMilliseconds))}\" r=\"2.5\" fill=\"#2563eb\"/>");
            svg.AppendLine($"  <circle cx=\"{FormatInvariant(x)}\" cy=\"{FormatInvariant(EfficiencyY(result.EfficiencyPercent))}\" r=\"2.5\" fill=\"#059669\"/>");

            if (result.BlockCount == 1 || result.BlockCount % 5 == 0)
            {
                svg.AppendLine($"  <text x=\"{FormatInvariant(x)}\" y=\"{efficiencyTop + efficiencyHeight + 24}\" text-anchor=\"middle\" class=\"label\">{result.BlockCount}</text>");
            }
        }

        svg.AppendLine($"  <text x=\"{left + plotWidth / 2}\" y=\"{efficiencyTop + efficiencyHeight + 55}\" text-anchor=\"middle\" class=\"chart-title\">Кількість блоків N</text>");
        svg.AppendLine("  <line x1=\"920\" y1=\"34\" x2=\"960\" y2=\"34\" stroke=\"#2563eb\" stroke-width=\"3\"/><text x=\"970\" y=\"39\" class=\"label\">час</text>");
        svg.AppendLine("  <line x1=\"1020\" y1=\"34\" x2=\"1060\" y2=\"34\" stroke=\"#059669\" stroke-width=\"3\"/><text x=\"1070\" y=\"39\" class=\"label\">ефективність</text>");
        svg.AppendLine("</svg>");

        File.WriteAllText(path, svg.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void AppendChartAxes(
        StringBuilder svg,
        double left,
        double plotWidth,
        double top,
        double height,
        double maximum,
        string title)
    {
        svg.AppendLine($"  <text x=\"{left}\" y=\"{top - 18}\" class=\"chart-title\">{title}</text>");

        for (int tick = 0; tick <= 5; tick++)
        {
            double value = maximum * tick / 5.0;
            double y = top + height - height * tick / 5.0;
            svg.AppendLine($"  <line x1=\"{left}\" y1=\"{FormatInvariant(y)}\" x2=\"{left + plotWidth}\" y2=\"{FormatInvariant(y)}\" class=\"grid\"/>");
            svg.AppendLine($"  <text x=\"{left - 12}\" y=\"{FormatInvariant(y + 4)}\" text-anchor=\"end\" class=\"label\">{value:F2}</text>");
        }

        svg.AppendLine($"  <line x1=\"{left}\" y1=\"{top}\" x2=\"{left}\" y2=\"{top + height}\" class=\"axis\"/>");
        svg.AppendLine($"  <line x1=\"{left}\" y1=\"{top + height}\" x2=\"{left + plotWidth}\" y2=\"{top + height}\" class=\"axis\"/>");
    }

    private static void PrintReport(
        BenchmarkConfiguration configuration,
        IReadOnlyList<DistributionResult> results,
        TimeSpan totalBenchmarkTime,
        TimeSpan totalCpuTime,
        string csvPath,
        string svgPath)
    {
        Console.WriteLine("Лабораторна робота №3 — аналіз кластеризації масиву");
        Console.WriteLine(new string('=', 76));
        Console.WriteLine($"Розмір масиву: {configuration.ArraySize:N0} елементів");
        Console.WriteLine($"Варіанти розподілу: N = 1..{configuration.MaxBlocks}");
        Console.WriteLine($"Запусків кожного варіанта: {configuration.RunCount}");
        Console.WriteLine($"Проходів масиву в одному запуску: {configuration.PassesPerRun}");
        Console.WriteLine($"Логічних процесорів: {Environment.ProcessorCount}");
        Console.WriteLine();
        Console.WriteLine(
            $"{"N",3} {"Сер. час, мс",14} {"Сер. CPU, мс",14} {"Мін., мс",11} " +
            $"{"Макс., мс",11} {"Прискорення",13} {"Ефект., %",11} {"Стан",10}");
        Console.WriteLine(new string('-', 104));

        foreach (DistributionResult result in results)
        {
            Console.WriteLine(
                $"{result.BlockCount,3} " +
                $"{result.AverageElapsedMilliseconds,14:F3} " +
                $"{result.AverageCpuMilliseconds,14:F3} " +
                $"{result.MinElapsedMilliseconds,11:F3} " +
                $"{result.MaxElapsedMilliseconds,11:F3} " +
                $"{result.Speedup,13:F3} " +
                $"{result.EfficiencyPercent,11:F2} " +
                $"{(result.IsCorrect ? "УСПІШНО" : "ПОМИЛКА"),10}");
        }

        Console.WriteLine();
        PrintAsciiChart(results);

        DistributionResult fastest = results.MinBy(
            result => result.AverageElapsedMilliseconds)!;

        Console.WriteLine();
        Console.WriteLine($"Найшвидший варіант: N = {fastest.BlockCount}, {fastest.AverageElapsedMilliseconds:F3} мс");
        Console.WriteLine($"Прискорення відносно N = 1: {fastest.Speedup:F3} раза");
        Console.WriteLine($"Ефективність найкращого варіанта: {fastest.EfficiencyPercent:F2} %");
        Console.WriteLine($"Загальний реальний час експерименту: {totalBenchmarkTime.TotalMilliseconds:F3} мс");
        Console.WriteLine($"Сумарний процесорний час експерименту: {totalCpuTime.TotalMilliseconds:F3} мс");
        Console.WriteLine($"CSV-таблиця: {csvPath}");
        Console.WriteLine($"SVG-графік: {svgPath}");
    }

    private static void PrintAsciiChart(IReadOnlyList<DistributionResult> results)
    {
        const int maximumBarWidth = 48;
        double maximumTime = results.Max(result => result.AverageElapsedMilliseconds);

        Console.WriteLine("Графік середнього часу (довша смуга = більший час)");
        foreach (DistributionResult result in results)
        {
            int barWidth = Math.Max(
                1,
                (int)Math.Round(result.AverageElapsedMilliseconds / maximumTime * maximumBarWidth));

            Console.WriteLine(
                $"N={result.BlockCount,2} |{new string('█', barWidth),-maximumBarWidth}| " +
                $"{result.AverageElapsedMilliseconds,8:F3} мс");
        }
    }

    private static BenchmarkConfiguration ParseArguments(string[] args)
    {
        int arraySize = DefaultArraySize;
        int maxBlocks = DefaultMaxBlocks;
        int runCount = DefaultRunCount;
        int passesPerRun = DefaultPassesPerRun;
        string outputDirectory = GetDefaultOutputDirectory();
        bool showHelp = false;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--size":
                    arraySize = ParsePositiveInt(ReadNextValue(args, ref index), "--size");
                    break;
                case "--max-blocks":
                    maxBlocks = ParsePositiveInt(ReadNextValue(args, ref index), "--max-blocks");
                    break;
                case "--runs":
                    runCount = ParsePositiveInt(ReadNextValue(args, ref index), "--runs");
                    break;
                case "--passes":
                    passesPerRun = ParsePositiveInt(ReadNextValue(args, ref index), "--passes");
                    break;
                case "--output":
                    outputDirectory = ReadNextValue(args, ref index);
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"невідомий параметр «{args[index]}»");
            }
        }

        return new BenchmarkConfiguration(
            arraySize,
            maxBlocks,
            runCount,
            passesPerRun,
            outputDirectory,
            showHelp);
    }

    private static void ValidateConfiguration(BenchmarkConfiguration configuration)
    {
        if (configuration.ArraySize < 1_000_000)
        {
            throw new ArgumentException("розмір масиву має бути не менше 1 000 000 елементів");
        }

        if (configuration.MaxBlocks > configuration.ArraySize)
        {
            throw new ArgumentException("кількість блоків не може перевищувати розмір масиву");
        }

        if (configuration.RunCount < 3)
        {
            throw new ArgumentException("кожний варіант потрібно виконати щонайменше 3 рази");
        }

        if (string.IsNullOrWhiteSpace(configuration.OutputDirectory))
        {
            throw new ArgumentException("папка результатів не може бути порожньою");
        }
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

    private static string GetDefaultOutputDirectory()
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "results"));
    }

    private static string FormatInvariant(double value)
    {
        return value.ToString("F6", CultureInfo.InvariantCulture);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Лабораторна робота №3");
        Console.WriteLine();
        Console.WriteLine("Параметри:");
        Console.WriteLine("  --size N          розмір масиву, щонайменше 1 000 000");
        Console.WriteLine("  --max-blocks N    перевірити всі варіанти від 1 до N");
        Console.WriteLine("  --runs N          кількість запусків варіанта, щонайменше 3");
        Console.WriteLine("  --passes N        проходів масиву в кожному запуску");
        Console.WriteLine("  --output PATH     папка для CSV і SVG");
        Console.WriteLine("  -h, --help        показати довідку");
        Console.WriteLine();
        Console.WriteLine("Приклад:");
        Console.WriteLine("  dotnet run -c Release --project .\\lab3\\Lab3.csproj -- --size 2000000 --max-blocks 50 --runs 5");
    }

    private readonly record struct ArrayPart(int StartIndex, int Length);

    private sealed record PartResult(long Sum);

    private sealed record TrialResult(long TotalSum, long ExpectedSum, TimeSpan Elapsed)
    {
        public bool IsCorrect => TotalSum == ExpectedSum;
    }

    private sealed class DistributionResult
    {
        public DistributionResult(
            int blockCount,
            double averageElapsedMilliseconds,
            double averageCpuMilliseconds,
            double minElapsedMilliseconds,
            double maxElapsedMilliseconds,
            bool isCorrect)
        {
            BlockCount = blockCount;
            AverageElapsedMilliseconds = averageElapsedMilliseconds;
            AverageCpuMilliseconds = averageCpuMilliseconds;
            MinElapsedMilliseconds = minElapsedMilliseconds;
            MaxElapsedMilliseconds = maxElapsedMilliseconds;
            IsCorrect = isCorrect;
        }

        public int BlockCount { get; }
        public double AverageElapsedMilliseconds { get; }
        public double AverageCpuMilliseconds { get; }
        public double MinElapsedMilliseconds { get; }
        public double MaxElapsedMilliseconds { get; }
        public bool IsCorrect { get; }
        public double Speedup { get; set; }
        public double EfficiencyPercent { get; set; }
    }

    private sealed record BenchmarkConfiguration(
        int ArraySize,
        int MaxBlocks,
        int RunCount,
        int PassesPerRun,
        string OutputDirectory,
        bool ShowHelp);
}
