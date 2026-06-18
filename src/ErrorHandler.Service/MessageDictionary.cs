namespace ErrorHandler.Service;

/// <summary>
/// Manages the state of active PLC alarms, tracks state transitions (New, Acknowledged, Resolved), 
/// and generates a change log delta for downstream processing.
/// </summary>
public class MessageDictionary
{
    // Holds the historical state of active errors using their unique 'Id' as the key.
    // Uses 'dynamic' to support duck-typing from TwinCAT ADS notification objects.
    private readonly Dictionary<string, dynamic> _cache = new Dictionary<string, dynamic>();

    /// <summary>
    /// Processes the current batch of active alarms from the PLC, updates the internal cache,
    /// and returns a list of human-readable changes (new alarms, acknowledgments, or resolutions).
    /// </summary>
    /// <param name="currentArray">A dynamic collection containing the current active alarms from the PLC.</param>
    /// <returns>A list of strings describing state changes since the last update.</returns>
    public List<string> UpdateAndGetChanges(dynamic currentArray)
    {
        var changeLogs = new List<string>();
        if (currentArray == null) return changeLogs;
        
        // Track IDs present in the current PLC reading to detect which alarms disappear later
        var currentIds = new HashSet<string>();

        // =======================================================================
        // State Mutation: Process New and Existing Alarms
        // =======================================================================
        foreach (dynamic currentError in currentArray)
        {
            // Parse PLC timestamp into a standard .NET DateTime structure
            var dt = ToDateTime(currentError.PLCTimeStamp);
            
            string id = currentError.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue; // Skip invalid entries without an identifier
            }
            currentIds.Add(id);

            // Check if this alarm was already active during the last scan cycle
            if (_cache.TryGetValue(id, out var oldError))
            {
                // CASE: Existing Alarm - Check if the acknowledgment state changed
                if (currentError.IsAcked != oldError.IsAcked)
                {
                    changeLogs.Add($"[ACKNOWLEDGED] {(ErrorType)currentError.ErrorType} '{id}' has been acknowledged");
                }
                
                // Update the cache with the newest state data
                _cache[id] = currentError;
            }
            else
            {
                // CASE: Brand New Alarm - Register it and log its details
                _cache.Add(id, currentError);
                changeLogs.Add($"[NEW {((ErrorType)currentError.ErrorType).ToString().ToUpper()}] | " +
                               $"'{id}' | " +
                               $"ErrorCode: {currentError.ErrorCode} | " +
                               $"{dt}");
            }
        }
        
        // =======================================================================
        // Delta Extraction: Detect Resolved Alarms
        // =======================================================================
        
        // Find any cached error IDs that are no longer present in the current PLC alarm array
        var solvedIds = _cache.Keys.Where(id => !currentIds.Contains(id)).ToList();

        foreach (var solvedId in solvedIds)
        {
            changeLogs.Add($"[SOLVED] '{solvedId}' has been resolved");
            
            // Remove from cache so it can trigger a [NEW] event if it fires again in the future
            _cache.Remove(solvedId);
        }
        
        return changeLogs;
    }
    
    /// <summary>
    /// Converts a dynamic PLC Windows/TwinCAT SYSTEMTIME structure into a .NET Utc DateTime.
    /// </summary>
    /// <param name="ts">The dynamic timestamp object containing wYear, wMonth, etc.</param>
    /// <returns>A valid Utc DateTime object, or DateTime.MinValue if parsing fails or components are 0.</returns>
    private static DateTime ToDateTime(dynamic ts)
    {
        try
        {
            if (ts == null) return DateTime.MinValue;
            
            // Explicitly cast raw dynamic properties to ushorts
            var year = (ushort)ts.wYear;
            var month = (ushort)ts.wMonth;
            var day = (ushort)ts.wDay;
            var hour = (ushort)ts.wHour;
            var minute = (ushort)ts.wMinute;
            var second = (ushort)ts.wSecond;
            var ms = (ushort)ts.wMilliseconds;

            // Zeroed out essential components indicate an uninitialized or empty PLC struct
            if (year == 0 || month == 0 || day == 0)
                return DateTime.MinValue;

            return new DateTime(year, month, day, hour, minute, second, ms, DateTimeKind.Utc);
        }
        catch (Exception)
        {
            // Fail safely to MinValue if fields don't exist on the dynamic object or are out of bounds (e.g. Day 32)
            return DateTime.MinValue; 
        }
    }
}