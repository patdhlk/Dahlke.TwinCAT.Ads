namespace Dahlke.EtherCAT.Diagnostics;

/// <summary>
/// Decodes Beckhoff EtherCAT product codes into human-readable device type names.
///
/// Beckhoff product code convention (validated against real hardware + qitechgmbh/control):
///   - Upper 16 bits: terminal number (e.g., 0x044C = 1100, 0x03F0 = 1008)
///   - Lower 16 bits: device family code
///     - 0x2C52: EK series (couplers/junctions)
///     - 0x3052: EL series (standard I/O terminals)
///     - 0x4052: EP series (IP67 box modules)
///     - 0x5052: EM series (extended modules)
///     - 0x1052: ES series (economy terminals)
///     - 0x6052: EJ series (plug-in modules)
///
/// Terminal number functional categories:
///   1xxx: Digital inputs (EL) or couplers (EK)
///   2xxx: Digital outputs
///   3xxx: Analog inputs
///   4xxx: Analog outputs
///   5xxx: Position measurement / encoders
///   6xxx: Communication interfaces
///   7xxx: Compact drive technology
///   9xxx: System terminals (power supply, fusing)
///
/// References:
///   - https://github.com/qitechgmbh/control (Rust EtherCAT HAL)
///   - https://infosys.beckhoff.com/content/1033/ethercatsystem/2469090827.html
/// </summary>
internal static class BeckhoffDeviceDecoder
{
    // Beckhoff EtherCAT Vendor ID
    private const uint BeckhoffVendorId = 0x2;

    // Known lower-16-bit family codes from product codes
    private const ushort FamilyCodeEK = 0x2C52; // EK: couplers/junctions
    private const ushort FamilyCodeEL = 0x3052; // EL: standard terminals
    private const ushort FamilyCodeEP = 0x4052; // EP: IP67 box modules
    private const ushort FamilyCodeEM = 0x5052; // EM: extended modules
    private const ushort FamilyCodeES = 0x1052; // ES: economy terminals
    private const ushort FamilyCodeEJ = 0x6052; // EJ: plug-in modules

    /// <summary>
    /// Decodes a Beckhoff product code into a device type string like "EK1100" or "EL2808".
    /// Returns the decoded name, or a generic description for non-Beckhoff or unknown devices.
    /// </summary>
    public static string DecodeDeviceType(uint vendorId, uint productCode)
    {
        if (vendorId != BeckhoffVendorId)
            return vendorId == 0 ? "Unknown" : $"Vendor(0x{vendorId:X})";

        uint terminalNumber = productCode >> 16;
        if (terminalNumber == 0)
            return "Unknown";

        ushort familyCode = (ushort)(productCode & 0xFFFF);
        string prefix = ResolveFamilyPrefix(familyCode, terminalNumber);

        return $"{prefix}{terminalNumber}";
    }

    /// <summary>
    /// Resolves the device series prefix (EK, EL, EP, etc.) from the product code's
    /// lower 16 bits (family code). Falls back to heuristic based on terminal number
    /// if the family code is unrecognized.
    /// </summary>
    private static string ResolveFamilyPrefix(ushort familyCode, uint terminalNumber)
    {
        // Primary: match on known family codes
        return familyCode switch
        {
            FamilyCodeEK => "EK",
            FamilyCodeEL => "EL",
            FamilyCodeEP => "EP",
            FamilyCodeEM => "EM",
            FamilyCodeES => "ES",
            FamilyCodeEJ => "EJ",
            // Fallback: guess from terminal number
            _ => GuessPrefixFromTerminalNumber(terminalNumber),
        };
    }

    /// <summary>
    /// Heuristic fallback when the family code is unrecognized.
    /// Uses the terminal number range to guess the prefix.
    /// </summary>
    private static string GuessPrefixFromTerminalNumber(uint terminalNumber) => terminalNumber switch
    {
        >= 1100 and <= 1199 => "EK", // EK1100-EK1199 are couplers
        _ => "EL", // Default to EL for standard I/O
    };
}
