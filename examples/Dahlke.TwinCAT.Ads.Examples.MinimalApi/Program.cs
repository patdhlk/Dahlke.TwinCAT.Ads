using System.Text.Json;
using Dahlke.TwinCAT.Ads;

// Pin the content root to the app directory so appsettings.json is found
// even when launched via `dotnet run --project` from the repo root.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Set "UseSimulation": false in appsettings.json to talk to a real PLC.
if (builder.Configuration.GetValue<bool>("UseSimulation"))
    builder.Services.AddTwinCatAdsSimulation(builder.Configuration);
else
    builder.Services.AddTwinCatAds(builder.Configuration);

var app = builder.Build();

// GET /plcs — list all configured PLC connections and their status.
app.MapGet("/plcs", (IAdsConnectionPool pool) =>
    pool.GetAllConnections().Select(kvp => new
    {
        PlcId = kvp.Key,
        kvp.Value.DisplayName,
        kvp.Value.IsConnected,
    }));

// GET /plcs/plc1/state — current ADS state (Run, Stop, Config, ...).
// GetConnection throws UnknownPlcTargetException for an unconfigured plcId;
// use TryGetConnection for the non-throwing lookup.
app.MapGet("/plcs/{plcId}/state", async (string plcId, IAdsConnectionPool pool, CancellationToken ct) =>
{
    if (!pool.TryGetConnection(plcId, out var connection))
        return Results.NotFound($"Unknown PLC '{plcId}'.");
    if (!connection.IsConnected)
        return Results.Problem($"PLC '{plcId}' is not connected.", statusCode: StatusCodes.Status503ServiceUnavailable);

    var state = await connection.GetAdsStateAsync(ct);
    return Results.Ok(new { plcId, State = state.ToString() });
});

// GET /plcs/plc1/symbols/GVL.Counter — read a symbol value.
app.MapGet("/plcs/{plcId}/symbols/{symbolPath}", async (string plcId, string symbolPath, IAdsConnectionPool pool, CancellationToken ct) =>
{
    if (!pool.TryGetConnection(plcId, out var connection))
        return Results.NotFound($"Unknown PLC '{plcId}'.");
    if (!connection.IsConnected)
        return Results.Problem($"PLC '{plcId}' is not connected.", statusCode: StatusCodes.Status503ServiceUnavailable);

    var value = await connection.ReadValueAsync(symbolPath, ct);
    return Results.Ok(new { symbolPath, value });
});

// PUT /plcs/plc1/symbols/GVL.Counter with body {"value": 42} — write a symbol value.
app.MapPut("/plcs/{plcId}/symbols/{symbolPath}", async (string plcId, string symbolPath, WriteRequest request, IAdsConnectionPool pool, CancellationToken ct) =>
{
    if (!pool.TryGetConnection(plcId, out var connection))
        return Results.NotFound($"Unknown PLC '{plcId}'.");
    if (!connection.IsConnected)
        return Results.Problem($"PLC '{plcId}' is not connected.", statusCode: StatusCodes.Status503ServiceUnavailable);

    var value = ToClrValue(request.Value);
    if (value is null)
        return Results.BadRequest("Body must be {\"value\": <bool|number|string>}.");

    await connection.WriteValueAsync(symbolPath, value, ct);
    return Results.Ok(new { symbolPath, value });
});

// POST /plcs/plc1/reconnect — force a reconnect of the connection loop.
app.MapPost("/plcs/{plcId}/reconnect", (string plcId, IAdsConnectionPool pool) =>
{
    if (!pool.TryGetConnection(plcId, out _))
        return Results.NotFound($"Unknown PLC '{plcId}'.");

    pool.ForceReconnect(plcId);
    return Results.Accepted();
});

app.Run();

// With a real PLC the written CLR type must match the PLC symbol type
// (e.g. a PLC INT needs a short) — extend this mapping for your symbols.
static object? ToClrValue(JsonElement element) => element.ValueKind switch
{
    JsonValueKind.True => true,
    JsonValueKind.False => false,
    JsonValueKind.String => element.GetString(),
    JsonValueKind.Number when element.TryGetInt32(out var i) => i,
    JsonValueKind.Number => element.GetDouble(),
    _ => null,
};

record WriteRequest(JsonElement Value);
