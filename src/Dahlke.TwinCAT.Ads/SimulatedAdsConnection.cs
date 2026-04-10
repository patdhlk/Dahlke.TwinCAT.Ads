using System.Collections.Concurrent;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Simulierte PLC-Verbindung für Offline-Entwicklung.
/// Hält alle Symbole im Speicher und simuliert eine laufende Wickelmaschine.
/// </summary>
public sealed class SimulatedAdsConnection : IAdsConnection, IDisposable
{
    private readonly ILogger<SimulatedAdsConnection> _logger;
    private readonly ConcurrentDictionary<string, object?> _symbols = new();
    private readonly Timer _timer;
    private readonly Random _rng = new();

    // Maschinen-Simulationszustand
    private bool _running;
    private uint _currentChamber = 1;
    private uint _currentWindings;
    private double _flyerAngle;
    private uint _targetWindings = 212;
    private uint _numberOfChambers = 3;

    public string PlcId { get; }
    public string DisplayName { get; }
    public bool IsConnected => true;

    public SimulatedAdsConnection(string plcId, string displayName, ILoggerFactory loggerFactory)
    {
        PlcId = plcId;
        DisplayName = displayName;
        _logger = loggerFactory.CreateLogger<SimulatedAdsConnection>();

        InitializeSymbols();
        _timer = new Timer(Simulate, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        _logger.LogInformation("Simulierte SPS-Verbindung {PlcId} gestartet", plcId);
    }

    private void InitializeSymbols()
    {
        // Fast symbols
        _symbols["PRGMain.fbMain.fbFeedSpindle.uiO_CntRounds"] = (ushort)0;
        _symbols["PRGMain.fbMain.fbFeedSpindle.uiO_CoilNumberAtWork"] = (ushort)1;
        _symbols["PRGMain.fbMain.fbFeedSpindle.rActModPosition"] = 0.0f;
        _symbols["PRGMain.fbMain.fbWindingMachine.sStatusText"] = "Simulation — Anlage bereit";
        _symbols["GVL_Visu.xEnableJobData"] = true;

        // Slow symbols - job data
        _symbols["GVL_Visu.xJobDataValid"] = true;
        _symbols["GVL_Visu.stActJobData.NumberOfChambers"] = (ushort)3;
        _symbols["GVL_Visu.stActJobData.WireThickness"] = 0.28f;
        _symbols["GVL_Visu.stActJobData.Flyer"] = 170.0f;
        _symbols["GVL_Visu.stActJobData.Dorn"] = 99.0f;
        _symbols["GVL_Visu.stActJobData.OffsetStartPos"] = false;

        // Machine state
        _symbols["PRGMain.fbMain.fbWindingMachine.enO_State"] = 0;
        _symbols["PRGMain.fbMain.fbWindingMachine.xVB_OpenCloseProtection"] = false;
        _symbols["PRGMain.fbMain.fbFeedSpindle.xVB_Brake"] = false;
        _symbols["PRGMain.fbMain.fbCoilChange.xVB_Valve"] = false;

        // Momentary buttons (all false)
        _symbols["GVL_Visu.xVB_StartAuto"] = false;
        _symbols["GVL_Visu.xVB_StopAuto"] = false;
        _symbols["GVL_Visu.xVB_Reset"] = false;
        _symbols["GVL_Visu.xVB_RestartSafetyGroup"] = false;
        _symbols["GVL_Visu.xVB_EndCycle"] = false;
        _symbols["GVL_Visu.xVB_ConfirmJobData"] = false;

        // Prepared job (empty initially)
        _symbols["GVL_Visu.stJobPrepared.NumberOfChambers"] = (ushort)0;
        _symbols["GVL_Visu.stJobPrepared.Flyer"] = 0.0f;
        _symbols["GVL_Visu.stJobPrepared.Dorn"] = 0.0f;
        _symbols["GVL_Visu.stJobPrepared.WireThickness"] = 0.0f;
        _symbols["GVL_Visu.stJobPrepared.OffsetStartPos"] = false;

        // Active coil routines (3 chambers with sample data)
        InitializeCoilRoutines("GVL_Visu.stActJobData.astCoilRoutines", 3);
        InitializeCoilRoutines("GVL_Visu.stJobPrepared.astCoilRoutines", 0);

        _numberOfChambers = 3;
    }

    private void InitializeCoilRoutines(string prefix, int activeChambers)
    {
        for (int i = 1; i <= 6; i++)
        {
            var p = $"{prefix}[{i}]";
            var isActive = i <= activeChambers;
            _symbols[$"{p}.MaxRpm"] = (short)(isActive ? 2400 : 0);
            _symbols[$"{p}.Windings"] = (ushort)(isActive ? (i == 1 ? 212 : 213) : 0);
            _symbols[$"{p}.ChamberThickness"] = isActive ? 8.0f : 0.0f;
            _symbols[$"{p}.ChamberDistance"] = isActive ? 16.0f : 0.0f;
            _symbols[$"{p}.StartPosFlyer"] = (ushort)(isActive ? 40 : 0);
            _symbols[$"{p}.StopPosFlyer"] = (ushort)(isActive ? (i == 3 ? 170 : 160) : 0);
            _symbols[$"{p}.OffsetWire"] = 0.0f;
            _symbols[$"{p}.ReleaseWirePostion"] = (short)(isActive ? -90 : 0);
            _symbols[$"{p}.SlowRpm"] = (short)(isActive ? 35 : 0);
            _symbols[$"{p}.Deceleration"] = isActive ? 70 : 0;
            _symbols[$"{p}.NumberLastWindings"] = (ushort)(isActive ? 23 : 0);
        }
    }

    private void Simulate(object? state)
    {
        // StartAuto-Knopf → starten
        if (_symbols.TryGetValue("GVL_Visu.xVB_StartAuto", out var startVal) && startVal is true)
        {
            if (!_running)
            {
                _running = true;
                _symbols["PRGMain.fbMain.fbWindingMachine.sStatusText"] = "Simulation — Wickelvorgang gestartet";
                _symbols["PRGMain.fbMain.fbWindingMachine.enO_State"] = 80;
            }
        }

        // StopAuto → stoppen
        if (_symbols.TryGetValue("GVL_Visu.xVB_StopAuto", out var stopVal) && stopVal is true)
        {
            if (_running)
            {
                _running = false;
                _symbols["PRGMain.fbMain.fbWindingMachine.sStatusText"] = "Simulation — Anlage gestoppt";
                _symbols["PRGMain.fbMain.fbWindingMachine.enO_State"] = 0;
            }
        }

        // ConfirmJobData → Prepared → Active kopieren
        if (_symbols.TryGetValue("GVL_Visu.xVB_ConfirmJobData", out var confirmVal) && confirmVal is true)
        {
            CopyPreparedToActive();
            _symbols["GVL_Visu.xVB_ConfirmJobData"] = false;
        }

        if (!_running) return;

        // Flyer rotiert
        _flyerAngle = (_flyerAngle + 12.5) % 360.0;
        _symbols["PRGMain.fbMain.fbFeedSpindle.rActModPosition"] = (float)_flyerAngle;

        // Windungen hochzählen
        _currentWindings++;
        _symbols["PRGMain.fbMain.fbFeedSpindle.uiO_CntRounds"] = (ushort)_currentWindings;
        _symbols["PRGMain.fbMain.fbFeedSpindle.uiO_CoilNumberAtWork"] = (ushort)_currentChamber;

        // Kammerwechsel bei Ziel
        var targetKey = $"GVL_Visu.stActJobData.astCoilRoutines[{_currentChamber}].Windings";
        if (_symbols.TryGetValue(targetKey, out var targetVal))
        {
            _targetWindings = Convert.ToUInt32(targetVal);
        }

        if (_currentWindings >= _targetWindings)
        {
            _currentWindings = 0;
            _currentChamber++;
            if (_currentChamber > _numberOfChambers)
            {
                _currentChamber = 1;
                _running = false;
                _symbols["PRGMain.fbMain.fbWindingMachine.sStatusText"] = "Simulation — Zyklus abgeschlossen";
                _symbols["PRGMain.fbMain.fbWindingMachine.enO_State"] = 0;
            }
            else
            {
                _symbols["PRGMain.fbMain.fbWindingMachine.sStatusText"] = $"Simulation — Kammer {_currentChamber} wird gewickelt";
            }
        }
        else
        {
            _symbols["PRGMain.fbMain.fbWindingMachine.sStatusText"] = $"Simulation — Wickelvorgang Kammer {_currentChamber}";
        }
    }

    private void CopyPreparedToActive()
    {
        // Globale Parameter kopieren
        foreach (var key in new[] { "NumberOfChambers", "Flyer", "Dorn", "WireThickness", "OffsetStartPos" })
        {
            if (_symbols.TryGetValue($"GVL_Visu.stJobPrepared.{key}", out var val))
                _symbols[$"GVL_Visu.stActJobData.{key}"] = val;
        }

        // Spulenroutinen kopieren
        for (int i = 1; i <= 6; i++)
        {
            foreach (var field in new[] { "MaxRpm", "Windings", "ChamberThickness", "ChamberDistance",
                "StartPosFlyer", "StopPosFlyer", "OffsetWire", "ReleaseWirePostion",
                "SlowRpm", "Deceleration", "NumberLastWindings" })
            {
                var src = $"GVL_Visu.stJobPrepared.astCoilRoutines[{i}].{field}";
                var dst = $"GVL_Visu.stActJobData.astCoilRoutines[{i}].{field}";
                if (_symbols.TryGetValue(src, out var val))
                    _symbols[dst] = val;
            }
        }

        // Kammerzahl aktualisieren
        if (_symbols.TryGetValue("GVL_Visu.stActJobData.NumberOfChambers", out var chambers))
        {
            _numberOfChambers = Convert.ToUInt32(chambers);
        }

        _currentChamber = 1;
        _currentWindings = 0;
        _symbols["PRGMain.fbMain.fbWindingMachine.sStatusText"] = "Simulation — Auftrag aktiviert";

        _logger.LogInformation("Simulation: Vorbereiteter Auftrag aktiviert ({Chambers} Kammern)", _numberOfChambers);
    }

    public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct)
    {
        // Coil-Routinen-Array als Ganzes lesen (StartseiteService erwartet ein Array)
        if (symbolPath == "GVL_Visu.stActJobData.astCoilRoutines" ||
            symbolPath == "GVL_Visu.stJobPrepared.astCoilRoutines")
        {
            return Task.FromResult<object?>(BuildCoilRoutineArray(symbolPath));
        }

        _symbols.TryGetValue(symbolPath, out var value);
        return Task.FromResult(value);
    }

    public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct)
    {
        _symbols[symbolPath] = value;
        return Task.CompletedTask;
    }

    public async Task<Dictionary<string, object?>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct)
    {
        var results = new Dictionary<string, object?>();
        foreach (var path in symbolPaths)
        {
            results[path] = (await ReadValueAsync(path, ct));
        }
        return results;
    }

    public Task WriteValuesAsync(Dictionary<string, object> values, CancellationToken ct)
    {
        foreach (var (path, value) in values)
            _symbols[path] = value;
        return Task.CompletedTask;
    }

    public Task<AdsState> GetAdsStateAsync(CancellationToken ct)
    {
        return Task.FromResult(AdsState.Run);
    }

    public Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct)
    {
        // Simulation unterstützt keine Subscriptions — Timer-basiertes Polling reicht
        return Task.FromResult<IDisposable>(new NoOpDisposable());
    }

    /// <summary>
    /// Baut ein 1-basiertes Array auf, das StartseiteService.ReadCoilRoutinesAsync erwartet.
    /// </summary>
    private Array BuildCoilRoutineArray(string prefix)
    {
        var items = new CoilRoutineStruct[6];
        for (int i = 0; i < 6; i++)
        {
            var p = $"{prefix}[{i + 1}]";
            items[i] = new CoilRoutineStruct
            {
                MaxRpm = GetSymbol<short>(p, "MaxRpm"),
                Windings = GetSymbol<ushort>(p, "Windings"),
                ChamberThickness = GetSymbol<float>(p, "ChamberThickness"),
                ChamberDistance = GetSymbol<float>(p, "ChamberDistance"),
                StartPosFlyer = GetSymbol<ushort>(p, "StartPosFlyer"),
                StopPosFlyer = GetSymbol<ushort>(p, "StopPosFlyer"),
                OffsetWire = GetSymbol<float>(p, "OffsetWire"),
                ReleaseWirePostion = GetSymbol<short>(p, "ReleaseWirePostion"),
                SlowRpm = GetSymbol<short>(p, "SlowRpm"),
                Deceleration = GetSymbol<int>(p, "Deceleration"),
                NumberLastWindings = GetSymbol<ushort>(p, "NumberLastWindings"),
            };
        }

        // 1-basiertes Array erstellen (wie PLC ARRAY[1..6])
        var array = Array.CreateInstance(typeof(CoilRoutineStruct), new[] { 6 }, new[] { 1 });
        for (int i = 0; i < 6; i++)
            array.SetValue(items[i], i + 1);
        return array;
    }

    private T GetSymbol<T>(string prefix, string field)
    {
        if (_symbols.TryGetValue($"{prefix}.{field}", out var val) && val is not null)
        {
            try { return (T)Convert.ChangeType(val, typeof(T)); }
            catch { }
        }
        return default!;
    }

    public void Dispose()
    {
        _timer.Dispose();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// Dynamisch lesbares Struct für die simulierten Spulenroutinen.
/// Property-Namen müssen exakt den PLC-Struct-Feldern entsprechen.
/// </summary>
public sealed class CoilRoutineStruct
{
    public short MaxRpm { get; set; }
    public ushort Windings { get; set; }
    public float ChamberThickness { get; set; }
    public float ChamberDistance { get; set; }
    public ushort StartPosFlyer { get; set; }
    public ushort StopPosFlyer { get; set; }
    public float OffsetWire { get; set; }
    public short ReleaseWirePostion { get; set; }
    public short SlowRpm { get; set; }
    public int Deceleration { get; set; }
    public ushort NumberLastWindings { get; set; }
}
