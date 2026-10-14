using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Lab6;

internal static class Program
{
    private const int DefaultPort = 5055;
    private const int DefaultTimeoutSeconds = 30;
    private const double DefaultTolerance = 1e-12;
    private const int DefaultMaximumIterations = 20_000;

    private static async Task Main(string[] args)
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

            await RunClusterCalculationAsync(configuration);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or SocketException or
                JsonException or InvalidOperationException or OperationCanceledException or
                TimeoutException)
        {
            Console.Error.WriteLine($"Помилка: {exception.Message}");
            Console.Error.WriteLine(
                "Переконайтеся, що slave-сервер ЛР5 запущено, або скористайтеся --help.");
            Environment.ExitCode = 2;
        }
    }

    private static async Task RunClusterCalculationAsync(ProgramConfiguration configuration)
    {
        Console.WriteLine("Лабораторна робота №6 — розподілене обчислення L1, L2 і L3");
        Console.WriteLine(new string('=', 76));
        Console.WriteLine($"Host-вузол: {Environment.MachineName}");
        Console.WriteLine($"Slave-адреса: {configuration.SlaveHost}:{configuration.Port}");
        Console.WriteLine("Синхронізація: бінарний SemaphoreSlim (початкове значення 0, максимум 1)");
        Console.WriteLine();

        using SemaphoreSlim resultReady = new(initialCount: 0, maxCount: 1);
        using CancellationTokenSource remoteTimeout =
            new(TimeSpan.FromSeconds(configuration.TimeoutSeconds));

        SlaveResponse? slaveResponse = null;
        Exception? slaveFailure = null;

        async Task ReceiveSlaveResultAsync()
        {
            try
            {
                slaveResponse = await SlaveClient.RequestL2Async(
                    configuration.SlaveHost,
                    configuration.Port,
                    remoteTimeout.Token);
            }
            catch (Exception exception)
            {
                slaveFailure = exception;
            }
            finally
            {
                // Release переводить бінарний семафор зі стану 0 у стан 1.
                resultReady.Release();
            }
        }

        Stopwatch totalTimer = Stopwatch.StartNew();

        Console.WriteLine("[Паралельно] Надсилаємо slave-вузлу запит на обчислення L2...");
        Task slaveWorker = ReceiveSlaveResultAsync();

        Console.WriteLine("[Задача 1] Host обчислює L1 для матриці БД2 5×5...");
        Stopwatch localTimer = Stopwatch.StartNew();
        EigenResult hostResult = EigenvalueSolver.FindLargestAlgebraic(
            MatrixData.CreateBd2(),
            configuration.Tolerance,
            configuration.MaximumIterations);
        localTimer.Stop();

        if (!hostResult.Converged)
        {
            throw new InvalidOperationException("обчислення L1 не збіглося за заданий ліміт ітерацій");
        }

        Console.WriteLine($"[Задача 1] L1 = {hostResult.Eigenvalue:F12}; " +
                          $"ітерацій: {hostResult.Iterations}; " +
                          $"час: {localTimer.Elapsed.TotalMilliseconds:F3} мс");
        Console.WriteLine($"[Задача 1] Нев’язка: {hostResult.ResidualNorm:E6}");
        Console.WriteLine();

        Console.WriteLine("[Синхронізація] Задачу 1 завершено. Host виконує WaitAsync() і чекає L2...");

        // Якщо slave ще працює, host блокується тут. Якщо результат уже готовий,
        // значення семафора дорівнює 1 і очікування завершується одразу.
        await resultReady.WaitAsync();
        await slaveWorker;

        if (slaveFailure is OperationCanceledException)
        {
            throw new TimeoutException(
                $"slave-вузол не відповів протягом {configuration.TimeoutSeconds} с");
        }

        if (slaveFailure is not null)
        {
            throw new IOException("не вдалося отримати L2 від slave-вузла", slaveFailure);
        }

        if (slaveResponse is null)
        {
            throw new IOException("slave-вузол завершив роботу без результату");
        }

        if (!slaveResponse.Success || slaveResponse.Eigenvalue is null)
        {
            throw new IOException($"slave-вузол повернув помилку: {slaveResponse.Message}");
        }

        double l1 = hostResult.Eigenvalue;
        double l2 = slaveResponse.Eigenvalue.Value;
        double l3;
        string selectedFormula;

        if (l1 > l2)
        {
            l3 = l1 * l1 * l2;
            selectedFormula = "L3 = (L1²) · L2, тому що L1 > L2";
        }
        else if (l1 < l2)
        {
            l3 = l1 * l2 * l2;
            selectedFormula = "L3 = L1 · (L2²), тому що L1 < L2";
        }
        else
        {
            // При рівності обидві задані формули дають однаковий результат.
            l3 = l1 * l1 * l1;
            selectedFormula = "L3 = L1³, тому що L1 = L2";
        }

        totalTimer.Stop();

        Console.WriteLine("[Синхронізація] Семафор отримано: результат L2 готовий.");
        Console.WriteLine($"[Задача 2] Slave-вузол: {slaveResponse.MachineName}");
        Console.WriteLine($"[Задача 2] L2 = {l2:F12}; " +
                          $"ітерацій: {slaveResponse.Iterations}; " +
                          $"час на slave: {slaveResponse.ElapsedMilliseconds:F3} мс");
        Console.WriteLine($"[Задача 2] Нев’язка: {slaveResponse.ResidualNorm:E6}");
        Console.WriteLine();
        Console.WriteLine($"[Задача 3] {selectedFormula}");
        Console.WriteLine();
        Console.WriteLine("Підсумкові результати host-комп’ютера");
        Console.WriteLine(new string('-', 48));
        Console.WriteLine($"L1 = {l1:F12}");
        Console.WriteLine($"L2 = {l2:F12}");
        Console.WriteLine($"L3 = {l3:F12}");
        Console.WriteLine($"Загальний реальний час: {totalTimer.Elapsed.TotalMilliseconds:F3} мс");
        Console.WriteLine("Статус: РОЗПОДІЛЕНЕ ОБЧИСЛЕННЯ ЗАВЕРШЕНО");
    }

    private static ProgramConfiguration ParseArguments(string[] args)
    {
        string slaveHost = "127.0.0.1";
        int port = DefaultPort;
        int timeoutSeconds = DefaultTimeoutSeconds;
        double tolerance = DefaultTolerance;
        int maximumIterations = DefaultMaximumIterations;
        bool showHelp = false;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--slave-host":
                    slaveHost = ReadNextValue(args, ref index);
                    break;
                case "--port":
                    port = ParsePort(ReadNextValue(args, ref index));
                    break;
                case "--timeout-seconds":
                    timeoutSeconds = ParsePositiveInt(
                        ReadNextValue(args, ref index),
                        "--timeout-seconds");
                    break;
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

        if (string.IsNullOrWhiteSpace(slaveHost))
        {
            throw new ArgumentException("--slave-host не може бути порожнім");
        }

        return new ProgramConfiguration(
            slaveHost,
            port,
            timeoutSeconds,
            tolerance,
            maximumIterations,
            showHelp);
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

    private static int ParsePort(string value)
    {
        int port = ParsePositiveInt(value, "--port");
        if (port > 65_535)
        {
            throw new ArgumentException("--port має бути в діапазоні 1..65535");
        }

        return port;
    }

    private static int ParsePositiveInt(string value, string parameterName)
    {
        if (!int.TryParse(value, out int result) || result <= 0)
        {
            throw new ArgumentException($"{parameterName} має бути додатним цілим числом");
        }

        return result;
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

    private static void PrintHelp()
    {
        Console.WriteLine("Лабораторна робота №6 — host-програма розподіленого кластера");
        Console.WriteLine();
        Console.WriteLine("Перед запуском на slave-вузлі запустіть сервер ЛР5:");
        Console.WriteLine("  dotnet run -c Release --project .\\lab5\\Lab5.csproj -- --server --port 5055");
        Console.WriteLine();
        Console.WriteLine("Параметри host-програми:");
        Console.WriteLine("  --slave-host HOST        IP або ім’я slave-вузла, типово 127.0.0.1");
        Console.WriteLine($"  --port N                 TCP-порт, типово {DefaultPort}");
        Console.WriteLine($"  --timeout-seconds N      час очікування, типово {DefaultTimeoutSeconds} с");
        Console.WriteLine("  --tolerance VALUE        точність обчислення L1, типово 1e-12");
        Console.WriteLine("  --max-iterations N       ліміт ітерацій L1, типово 20000");
        Console.WriteLine("  -h, --help               показати довідку");
        Console.WriteLine();
        Console.WriteLine("Приклад:");
        Console.WriteLine("  dotnet run -c Release --project .\\lab6\\Lab6.csproj -- --slave-host 192.168.1.50 --port 5055");
    }

    private sealed record ProgramConfiguration(
        string SlaveHost,
        int Port,
        int TimeoutSeconds,
        double Tolerance,
        int MaximumIterations,
        bool ShowHelp);
}
