using System.Reactive.Disposables;
using System.Reactive.Linq;
using Dahlke.TwinCAT.Ads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ErrorHandler.Service;

// =======================================================================
// CLI Argument Validation & Help Interception
// =======================================================================

// Intercept help flags immediately to display usage instructions and exit.
if (args.Contains("--help") || args.Contains("-h"))
{
    ShowHelp();
    return;
}

// Locate the '--path' argument and extract the subsequent value if available.
var pathIndex = Array.IndexOf(args, "--path");
string? path = null;

if (pathIndex != -1 && pathIndex < args.Length - 1)
{
    path = args[pathIndex + 1];
}

// CRITICAL VALIDATION: Terminate startup if no valid TwinCAT variable path is specified.
if (string.IsNullOrWhiteSpace(path))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Error: Missing required argument '--path'.");
    Console.ResetColor();
    Console.WriteLine();
    ShowHelp();
    Environment.ExitCode = 1; // Return non-zero code to indicate execution failure
    return;
}

// =======================================================================
// Host Runtime & Logging Infrastructure Configuration
// =======================================================================

// Initialize the generic host builder which manages DI, configuration, and logging lifecycles.
var builder = Host.CreateApplicationBuilder(args);

// Purge default logging providers (e.g., EventLog) and restrict diagnostics to the standard console.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// =======================================================================
// Communication Environment Environment Strategy Selection
// =======================================================================

// Select the connectivity layer depending on whether hardware or simulation is requested.
var useRealPlc = args.Contains("--real");
if (useRealPlc)
    builder.Services.AddTwinCatAds(builder.Configuration); // Binds to actual AMS Router hardware channels
else
    builder.Services.AddTwinCatAdsSimulation(builder.Configuration); // Binds to a virtual localized mock engine

// =======================================================================
// Dependency Registration & Core Hosted Service Provisioning
// =======================================================================

// Register the monitoring background service using a factory delegate to pass the validated CLI path.
builder.Services.AddHostedService<ErrorHandlerService>(sp =>
    new ErrorHandlerService(
        sp.GetRequiredService<IAdsConnectionPool>(),
        sp.GetRequiredService<ILogger<ErrorHandlerService>>(),
        path
    ));

// =======================================================================
// Application Initialization & Runtime Execution
// =======================================================================

// Construct the host container and start running the background service loops asynchronously.
using var host = builder.Build();
await host.RunAsync();
return;


// Outputs the command-line usage syntax and argument specifications to the standard console.
static void ShowHelp()
{
    Console.WriteLine("=======================================================================");
    Console.WriteLine(" PLC Monitor Service - Error Handler");
    Console.WriteLine("=======================================================================");
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run -- --path <symbol_path> [options]");
    Console.WriteLine();
    Console.WriteLine("Required Arguments:");
    Console.WriteLine("  --path <string>    The global variable/array path in TwinCAT to subscribe to.");
    Console.WriteLine("                     Example: --path \"GVL.MyAlarmArray\"");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --real             Connects to a physical PLC via AMS router hardware.");
    Console.WriteLine("                     If omitted, the service boots into a local simulation.");
    Console.WriteLine("  -h, --help         Display this help message and exit.");
    Console.WriteLine("=======================================================================");
}

namespace ErrorHandler.Service
{
    /// <summary>
    /// A background service responsible for maintaining the ADS connection to the PLC,
    /// subscribing to the designated alarm array, and routing incoming updates to the state dictionary.
    /// </summary>
    /// <param name="pool">The active pool managing connection routes to TwinCAT ADS endpoints.</param>
    /// <param name="logger">The structured logging instance for diagnostic outputs.</param>
    /// <param name="path">The validated global variable string pathway inside the targeted PLC.</param>
    public sealed class ErrorHandlerService(IAdsConnectionPool pool, ILogger<ErrorHandlerService> logger, string path)
        : IHostedService
    {
        private readonly MessageDictionary _messageDictionary = new();
        private IDisposable? _subscription;

        /// <summary>
        /// Triggered when the application host has fully started. Handles connection initialization,
        /// awaits PLC readiness targets, and establishes the reactive subscription pipeline stream.
        /// </summary>
        /// <param name="cancellationToken">Indicates that the start process has been aborted.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous startup operation.</returns>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("PLC Monitor Service is starting...");

            // Diagnostic Step: Log the localized connectivity states of all registered router routes.
            foreach (var (plcId, conn) in pool.GetAllConnections())
            {
                logger.LogInformation("PLC Node: {PlcId} ({DisplayName}) | Connected: {IsConnected}",
                    plcId, conn.DisplayName, conn.IsConnected);
            }

            // Target the specific pre-configured primary target identifier within the pool.
            var connection = pool.GetConnection("plc1");

            // Limit connection acquisition time to 10 seconds to prevent indefinite application hangs.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            try
            {
                // Await Connection Loop: Spin safely until the ADS subsystem confirms handshake resolution.
                while (!connection.IsConnected)
                {
                    timeoutCts.Token.ThrowIfCancellationRequested();
                    await Task.Delay(100, timeoutCts.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Graceful Fallback: Allow the stream runtime to manage link discovery if initialization times out.
                logger.LogWarning("Timeout reached waiting for 'plc1' connection. Proceeding anyway...");
            }

            logger.LogInformation("Connection 'plc1' readiness: {IsConnected}", connection.IsConnected);

            // Fetch and evaluate the operational status of the remote TwinCAT execution engine.
            var state = await connection.GetAdsStateAsync(cancellationToken);
            logger.LogInformation("ADS State: {State}", state);

            // =======================================================================
            // Reactive Notification Subscription Engine
            // =======================================================================

            // Create the cold observable data stream listening to the requested variable array.
            var alarmObservable = ObserveValue(connection, path, cycleTimeMs: 200);

            // Subscribe to the stream events using Reactive Extensions (Rx).
            _subscription = alarmObservable
                .Select(data => data.Values) // Flatten the subscription notification payload down to the inner array
                .Subscribe(
                    (currentArray) =>
                    {
                        // Match current array layout against the state dictionary to isolate chronological deltas.
                        IReadOnlyCollection<string> changes = _messageDictionary.UpdateAndGetChanges(currentArray);
                        if (changes.Count <= 0) return; // Optimize out execution early if no new state shifts occur

                        // Log structural header block via system logger infrastructure.
                        Console.WriteLine("--- Update Received ---");

                        foreach (var logLine in changes)
                        {
                            Console.WriteLine(logLine);
                        }
                    },
                    ex => logger.LogError(ex, "ADS Subscription encountered an error."), // Handles streaming, network, or type marshal drops
                    () => logger.LogInformation("Subscription stream completed.") // Handles smooth channel termination steps
                );

            logger.LogInformation("[MONITORING ACTIVE] Service running in background.");
        }

        /// <summary>
        /// Triggered when the application host is performing a graceful shutdown.
        /// Disposes active reactive handles to prevent memory leaks and clear active network callbacks.
        /// </summary>
        /// <param name="cancellationToken">Indicates that the shutdown process should no longer be graceful.</param>
        /// <returns>A Completed <see cref="Task"/>.</returns>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("PLC Monitor Service is stopping...");

            // Explicitly tear down active notification listeners.
            _subscription?.Dispose();

            return Task.CompletedTask;
        }

        /// <summary>
        /// Wraps the callback-driven, low-level TwinCAT notification subscription infrastructure into an elegant Rx Observable stream.
        /// </summary>
        /// <param name="conn">The active ADS connection target interface.</param>
        /// <param name="symbolPath">The specific symbol variable context path inside the runtime target.</param>
        /// <param name="cycleTimeMs">How frequently the underlying ADS router pushes runtime data changes.</param>
        /// <returns>A cold observable stream emitting structural notification tuples containing symbol paths and dynamic values.</returns>
        private static IObservable<(string Symbol, dynamic Values)> ObserveValue(
            IAdsConnection conn, string symbolPath, int cycleTimeMs = 200)
        {
            return Observable.Create<(string, dynamic Values)>(async (observer, ct) =>
            {
                try
                {
                    // Hook into the underlying TwinCAT ADS data monitoring event loops.
                    return await conn.SubscribeAsync<dynamic>(
                        symbolPath,
                        cycleTimeMs,
                        (sym, val) => observer.OnNext((sym, val)!), // Directly push updates straight down the pipeline
                        ct);
                }
                catch (Exception e)
                {
                    // Propagate structural connection errors straight down the pipeline to error handlers.
                    observer.OnError(e);
                    return Disposable.Empty;
                }
            });
        }
    }
}
