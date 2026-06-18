namespace ErrorHandler.Service;

public enum ErrorType
{
    None	= 0,
    Info    = 1,			
    Warning = 2,
    Error   = 3
}

public struct ErrorEntry
{
    public string Key;
    
    public string Id;

    public uint ErrorCode;

    public int ErrorType;
    
    public bool IsActive;
    
    public bool NeedsAck;
    
    public bool IsAcked;
    
    public TimeStruct PlcTimeStamp;
}

public struct TimeStruct
{
    public ushort Year;
    public ushort Month;
    public ushort DayOfWeek;
    public ushort Day;
    public ushort Hour;
    public ushort Minute;
    public ushort Second;
    public ushort Milliseconds;
}