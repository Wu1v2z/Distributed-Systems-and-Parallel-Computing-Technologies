using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Lab5;

internal static class Program
{
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

            switch (configuration.Mode)
            {
                case RunMode.Compute:
                    ComputeLocally(configuration);
                    break;
                case RunMode.Server:
                    await RunServerAsync(configuration);
                    break;
                case RunMode.Request:
                    await RequestFromSlaveAsync(configuration);
                    break;
                default:
                    throw new InvalidOperationException("невідомий режим запуску");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or SocketException or OperationCanceledException)
        {
            Console.Error.WriteLine($"Помилка: {exception.Message}");
            Console.Error.WriteLine("Використайте --help для перегляду параметрів.");
            Environment.ExitCode = 2;
        }
    }

    private static void ComputeLocally(ProgramConfiguration configuration)
    {
        double[,] matrix = MatrixData.CreateBd1();
        Stopwatch timer = Stopwatch.StartNew();
        EigenResult result = EigenvalueSolver.FindLargestAlgebraic(
            matrix,
            configuration.Tolerance,
            configuration.MaximumIterations);
        timer.Stop();

        PrintResult(
            "Лабораторна робота №5 — обчислення L2 на slave-вузлі",
            Environment.MachineName,
            matrix,
            result,
            timer.Elapsed.TotalMilliseconds);

        if (!result.Converged)
        {
            Environment.ExitCode = 1;
        }
    }

    private static async Task RunServerAsync(ProgramConfiguration configuration)
    {
        if (!IPAddress.TryParse(configuration.BindAddress, out IPAddress? bindAddress))
        {
            throw new ArgumentException(
                $"--bind має бути IP-адресою, отримано «{configuration.BindAddress}»");
        }

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            await SlaveProtocol.RunServerAsync(
                bindAddress,
                configuration.Port,
                configuration.OneRequestOnly,
                configuration.Tolerance,
                configuration.MaximumIterations,
                cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task RequestFromSlaveAsync(ProgramConfiguration configuration)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        SlaveResponse response = await SlaveProtocol.RequestResultAsync(
            configuration.RequestHost!,
            configuration.Port,
            timeout.Token);

        Console.WriteLine("ЛР5 — перевірка отримання L2 від slave-вузла");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"Slave-вузол: {response.MachineName}");
        Console.WriteLine($"ОС slave-вузла: {response.OperatingSystem}");
        Console.WriteLine($"Час UTC: {response.ComputedAtUtc:O}");
        Console.WriteLine($"Повідомлення: {response.Message}");

        if (!response.Success || response.Eigenvalue is null)
        {
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"L2 = {response.Eigenvalue:F12}");
        Console.WriteLine($"Ітерацій: {response.Iterations}");
        Console.WriteLine($"Норма нев’язки: {response.ResidualNorm:E6}");
        Console.WriteLine($"Час на slave-вузлі: {response.ElapsedMilliseconds:F3} мс");
        Console.WriteLine("Статус: РЕЗУЛЬТАТ ОТРИМАНО");
    }

    private static void PrintResult(
        string title,
        string machineName,
        double[,] matrix,
        EigenResult result,
        double elapsedMilliseconds)
    {
        Console.WriteLine(title);
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"Вузол обчислення: {machineName}");
        Console.WriteLine($"Матриця БД1: {matrix.GetLength(0)}×{matrix.GetLength(1)}");
        Console.WriteLine("Метод: зміщений степеневий метод із часткою Релея");
        Console.WriteLine($"Нижня межа Гершгоріна: {result.LowerGershgorinBound:F6}");
        Console.WriteLine($"Діагональний зсув: {result.Shift:F6}");
        Console.WriteLine($"Власне число зміщеної матриці: {result.ShiftedEigenvalue:F12}");
        Console.WriteLine();
        Console.WriteLine($"L2 = {result.Eigenvalue:F12}");
        Console.WriteLine(
            $"Власний вектор: ({string.Join("; ", result.Eigenvector.Select(value => value.ToString("F9")))})");
        Console.WriteLine($"Кількість ітерацій: {result.Iterations}");
        Console.WriteLine($"Норма нев’язки ||Av - L2*v||₂: {result.ResidualNorm:E6}");
        Console.WriteLine($"Час обчислення: {elapsedMilliseconds:F3} мс");
        Console.WriteLine($"Статус: {(result.Converged ? "ЗБІЖНІСТЬ ДОСЯГНУТО" : "ЛІМІТ ІТЕРАЦІЙ")}");
    }

    private static ProgramConfiguration ParseArguments(string[] args)
    {
        RunMode mode = RunMode.Compute;
        bool modeWasSelected = false;
        string? requestHost = null;
        string bindAddress = "0.0.0.0";
        int port = SlaveProtocol.DefaultPort;
        bool oneRequestOnly = false;
        double tolerance = DefaultTolerance;
        int maximumIterations = DefaultMaximumIterations;
        bool showHelp = false;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--server":
                    SelectMode(ref mode, ref modeWasSelected, RunMode.Server);
                    break;
                case "--request":
                    SelectMode(ref mode, ref modeWasSelected, RunMode.Request);
                    requestHost = ReadNextValue(args, ref index);
                    break;
                case "--bind":
                    bindAddress = ReadNextValue(args, ref index);
                    break;
                case "--port":
                    port = ParsePort(ReadNextValue(args, ref index));
                    break;
                case "--once":
                    oneRequestOnly = true;
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

        if (oneRequestOnly && mode != RunMode.Server)
        {
            throw new ArgumentException("--once можна використовувати лише разом із --server");
        }

        return new ProgramConfiguration(
            mode,
            requestHost,
            bindAddress,
            port,
            oneRequestOnly,
            tolerance,
            maximumIterations,
            showHelp);
    }

    private static void SelectMode(
        ref RunMode currentMode,
        ref bool modeWasSelected,
        RunMode requestedMode)
    {
        if (modeWasSelected && currentMode != requestedMode)
        {
            throw new ArgumentException("--server і --request не можна використовувати одночасно");
        }

        currentMode = requestedMode;
        modeWasSelected = true;
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
        Console.WriteLine("Лабораторна робота №5 — обчислення L2 на slave-вузлі");
        Console.WriteLine();
        Console.WriteLine("Режими:");
        Console.WriteLine("  без параметрів            обчислити L2 на поточному вузлі");
        Console.WriteLine("  --server                  запустити TCP slave-сервер");
        Console.WriteLine("  --request HOST            отримати L2 від slave-сервера");
        Console.WriteLine();
        Console.WriteLine("Параметри:");
        Console.WriteLine("  --bind IP                 адреса slave-сервера, типово 0.0.0.0");
        Console.WriteLine($"  --port N                  TCP-порт, типово {SlaveProtocol.DefaultPort}");
        Console.WriteLine("  --once                    завершити сервер після одного запиту");
        Console.WriteLine("  --tolerance VALUE         точність, типово 1e-12");
        Console.WriteLine("  --max-iterations N        ліміт ітерацій, типово 20000");
        Console.WriteLine("  -h, --help                показати довідку");
        Console.WriteLine();
        Console.WriteLine("Приклади:");
        Console.WriteLine("  dotnet run -c Release --project .\\lab5\\Lab5.csproj");
        Console.WriteLine("  dotnet run -c Release --project .\\lab5\\Lab5.csproj -- --server --port 5055");
        Console.WriteLine("  dotnet run -c Release --project .\\lab5\\Lab5.csproj -- --request 192.168.1.50 --port 5055");
    }

    private enum RunMode
    {
        Compute,
        Server,
        Request
    }

    private sealed record ProgramConfiguration(
        RunMode Mode,
        string? RequestHost,
        string BindAddress,
        int Port,
        bool OneRequestOnly,
        double Tolerance,
        int MaximumIterations,
        bool ShowHelp);
}
