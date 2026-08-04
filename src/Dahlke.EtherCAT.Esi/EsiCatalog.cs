using System.Collections.Concurrent;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dahlke.EtherCAT.Esi;

/// <summary>
/// Runtime ESI catalog. Resolves a scanned identity to its device description by streaming
/// ranked candidate files, and caches the outcome — success or failure — per identity. The only
/// filesystem toucher in the EtherCAT feature; every failure mode collapses to a cached status.
/// </summary>
internal sealed class EsiCatalog : IEsiCatalog
{
    private readonly string? _directory;
    private readonly TimeSpan _budget;
    private readonly TimeProvider _clock;
    private readonly ILogger<EsiCatalog> _logger;

    /// <summary>
    /// Keyed on identity, holding a <see cref="Lazy{T}"/> of the resolve rather than the result.
    /// That wrapper is load-bearing twice over: it makes "parsed at most once per process" and
    /// "logged at most once per device" true BY CONSTRUCTION, and without it two concurrent first
    /// lookups of one device would both parse and both log.
    /// </summary>
    private readonly ConcurrentDictionary<EsiKey, Lazy<Task<EsiLookupResult>>> _cache = new();

    /// <summary>
    /// Creates the catalog. Logs once, here, if the configured directory is missing or absent, or
    /// paired with a lookup budget too small to ever bound a lookup — see the class doc and
    /// <see cref="EsiOptions.LookupBudgetMs"/>.
    /// </summary>
    public EsiCatalog(
        IOptions<EsiOptions> options, ILogger<EsiCatalog> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _clock = timeProvider ?? TimeProvider.System;
        _directory = options.Value.Directory;
        _budget = TimeSpan.FromMilliseconds(Math.Max(0, options.Value.LookupBudgetMs));

        // Emitted once here, never per slave: a missing directory is a deployment fact about the
        // process, and one line per slave per poll would drown the log.
        if (string.IsNullOrWhiteSpace(_directory))
        {
            _logger.LogInformation(
                "No ESI directory configured; EtherCAT slave detail will report esi: null with esiStatus: notConfigured.");
        }
        else if (!Directory.Exists(_directory))
        {
            _logger.LogWarning(
                "Configured ESI directory {Directory} does not exist; EtherCAT slave detail will report esi: null with esiStatus: notConfigured.",
                _directory);
        }
        // A directory that DOES exist but a budget that cannot bound anything is the silently-inert
        // combination EsiOptions.LookupBudgetMs documents: _budget is exhausted before the first
        // candidate file is ever opened, so every lookup reports the device as not found while the
        // directory looks fully configured. Mirrors EtherCatMonitor's own non-positive-budget
        // Warning (PollMasterAsync) — same reasoning, same "raise it" remedy.
        else if (options.Value.LookupBudgetMs <= 0)
        {
            _logger.LogWarning(
                "EtherCat:Esi:LookupBudgetMs is {BudgetMs} ms, which cannot bound a lookup — every " +
                "ESI resolution will report the device as not found. Raise it to a positive value if " +
                "ESI enrichment should ever resolve.",
                options.Value.LookupBudgetMs);
        }
    }

    /// <inheritdoc/>
    public Task<EsiLookupResult> LookupAsync(EsiKey key, string typeHint) =>
        _cache.GetOrAdd(
            key,
            _ => new Lazy<Task<EsiLookupResult>>(() => ResolveAsync(key, typeHint))).Value;

    private async Task<EsiLookupResult> ResolveAsync(EsiKey key, string typeHint)
    {
        if (string.IsNullOrWhiteSpace(_directory) || !Directory.Exists(_directory))
        {
            return EsiLookupResult.NotConfigured;
        }

        bool anyFileFailed = false;

        try
        {
            var budget = new LookupBudget(_clock, _clock.GetTimestamp(), _budget);

            IReadOnlyList<string> candidates =
                EsiCandidateRanker.Rank(Directory.EnumerateFiles(_directory, "*.xml"), typeHint);

            foreach (string file in candidates)
            {
                if (budget.Exhausted)
                {
                    _logger.LogWarning(
                        "ESI lookup for vendor 0x{VendorId:X}/product 0x{ProductCode:X} hit the " +
                        "{BudgetMs} ms LookupBudgetMs before searching every candidate file; " +
                        "reporting the device as not found. Raise EtherCat:Esi:LookupBudgetMs if " +
                        "it should resolve.",
                        key.VendorId, key.ProductCode, _budget.TotalMilliseconds);

                    return EsiLookupResult.NotFound;
                }

                try
                {
                    EsiDevice? device = await EsiDeviceReader
                        .TryReadAsync(file, key).ConfigureAwait(false);

                    if (device is not null)
                    {
                        return new EsiLookupResult(device, EsiStatus.Resolved);
                    }
                }
                catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
                {
                    // Skip and keep searching: one unreadable vendor file must not hide every
                    // other device in the set. The flag below is why this still cannot be
                    // reported as a clean absence.
                    anyFileFailed = true;
                    _logger.LogWarning(ex, "Skipping unreadable ESI file {File}.", file);
                }
            }

            // Absence cannot honestly be claimed over a file that was never read.
            if (anyFileFailed)
            {
                return EsiLookupResult.ReadFailed;
            }

            _logger.LogInformation(
                "No ESI entry for vendor 0x{VendorId:X}/product 0x{ProductCode:X}; slave detail will report esi: null with esiStatus: notFound.",
                key.VendorId, key.ProductCode);

            return EsiLookupResult.NotFound;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to enumerate ESI directory {Directory}.", _directory);
            return EsiLookupResult.ReadFailed;
        }
        catch (Exception ex)
        {
            // Belt and braces, and NOT redundant: Lazy<Task<T>> caches a FAULTED task and
            // re-throws it on every later await of the same key, so an exception escaping here
            // would poison this device for the process lifetime and fault every future request
            // for it. Collapse to a status instead.
            _logger.LogWarning(
                ex,
                "Unexpected failure resolving ESI for vendor 0x{VendorId:X}/product 0x{ProductCode:X}.",
                key.VendorId, key.ProductCode);

            return EsiLookupResult.ReadFailed;
        }
    }

    /// <summary>
    /// One cold resolve's wall-clock allowance, checked between candidate files. Deliberately not
    /// checked mid-file: a single pathological 36 MB file can overrun it, which is a documented
    /// limitation rather than an oversight. Mirrors <c>EtherCatMonitor.CycleBudget</c>.
    /// </summary>
    private readonly record struct LookupBudget(TimeProvider Clock, long StartedAt, TimeSpan Limit)
    {
        internal bool Exhausted => Clock.GetElapsedTime(StartedAt) >= Limit;
    }
}
