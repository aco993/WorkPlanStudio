namespace WorkPlanStudio.WorkingTime;

/// <summary>
/// The sixteen German federal states. Public holidays beyond the nine nationwide
/// ones are set by state law, so a plant's state decides which days §9 ArbZG
/// closes. Values are the official two-letter abbreviations.
/// </summary>
public enum GermanState
{
    /// <summary>Baden-Württemberg.</summary>
    BW,
    /// <summary>Bayern.</summary>
    BY,
    /// <summary>Berlin.</summary>
    BE,
    /// <summary>Brandenburg.</summary>
    BB,
    /// <summary>Bremen.</summary>
    HB,
    /// <summary>Hamburg.</summary>
    HH,
    /// <summary>Hessen.</summary>
    HE,
    /// <summary>Mecklenburg-Vorpommern.</summary>
    MV,
    /// <summary>Niedersachsen.</summary>
    NI,
    /// <summary>Nordrhein-Westfalen.</summary>
    NW,
    /// <summary>Rheinland-Pfalz.</summary>
    RP,
    /// <summary>Saarland.</summary>
    SL,
    /// <summary>Sachsen.</summary>
    SN,
    /// <summary>Sachsen-Anhalt.</summary>
    ST,
    /// <summary>Schleswig-Holstein.</summary>
    SH,
    /// <summary>Thüringen.</summary>
    TH
}

/// <summary>Official German names of the states — proper nouns, so not translated.</summary>
public static class GermanStates
{
    /// <summary>All sixteen states in alphabetical order of their German name.</summary>
    public static IReadOnlyList<GermanState> All { get; } =
    [
        GermanState.BW, GermanState.BY, GermanState.BE, GermanState.BB, GermanState.HB, GermanState.HH,
        GermanState.HE, GermanState.MV, GermanState.NI, GermanState.NW, GermanState.RP, GermanState.SL,
        GermanState.SN, GermanState.ST, GermanState.SH, GermanState.TH
    ];

    /// <summary>The official German name, e.g. "Nordrhein-Westfalen".</summary>
    public static string Name(this GermanState state) => state switch
    {
        GermanState.BW => "Baden-Württemberg",
        GermanState.BY => "Bayern",
        GermanState.BE => "Berlin",
        GermanState.BB => "Brandenburg",
        GermanState.HB => "Bremen",
        GermanState.HH => "Hamburg",
        GermanState.HE => "Hessen",
        GermanState.MV => "Mecklenburg-Vorpommern",
        GermanState.NI => "Niedersachsen",
        GermanState.NW => "Nordrhein-Westfalen",
        GermanState.RP => "Rheinland-Pfalz",
        GermanState.SL => "Saarland",
        GermanState.SN => "Sachsen",
        GermanState.ST => "Sachsen-Anhalt",
        GermanState.SH => "Schleswig-Holstein",
        GermanState.TH => "Thüringen",
        _ => state.ToString()
    };
}
