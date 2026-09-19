namespace Arsenal.UI.Theming;

/// <summary>What the user picked in Settings. Stored in the <c>theme</c> config key.</summary>
public enum AppTheme
{
    System = 0,
    Dark = 1,
    Light = 2,

    /// <summary>
    /// The loud one. Black ground, hard corners, cyan signal, glow on anything live.
    /// </summary>
    Arsenal = 3,
}

/// <summary>
/// Every value a theme owns, in one object.
/// </summary>
/// <remarks>
/// The app used to have a single boolean axis - light or not - and it was enough while
/// the two themes differed only in colour. A theme that also changes corner radius,
/// border weight, type and whether surfaces glow has no boolean to be, so the axis is
/// this record instead and <c>App.ApplyConfiguredTheme</c> writes it into the
/// application's resources. Nothing outside this file decides what a theme looks like.
///
/// <para><see cref="IsLight"/> survives because two things underneath still only
/// understand light and dark: WPF UI's own control theme, and the DWM attribute that
/// colours the title bar.</para>
/// </remarks>
public sealed record ThemePalette
{
    /// <summary>Which of the two underlying control themes this one is built on.</summary>
    public required bool IsLight { get; init; }

    public required string SurfaceBase { get; init; }
    public required string SurfaceLayer { get; init; }
    public required string SurfaceCard { get; init; }
    public required string SurfaceCardHover { get; init; }
    public required string SurfaceSunken { get; init; }
    public required string StrokeSubtle { get; init; }
    public required string StrokeDivider { get; init; }
    public required string TextPrimary { get; init; }
    public required string TextSecondary { get; init; }
    public required string TextTertiary { get; init; }
    public required string NavigationInk { get; init; }
    public required string StatusSuccess { get; init; }
    public required string StatusWarning { get; init; }
    public required string StatusCritical { get; init; }
    public required string DividerLine { get; init; }

    /// <summary>The two window grounds when the user has turned transparency off.</summary>
    public required string OpaqueSidebar { get; init; }
    public required string OpaqueContent { get; init; }

    /// <summary>The wash over the Mica backdrop when transparency is on.</summary>
    public required string MicaContentWash { get; init; }

    /// <summary>The card colour the selected-row blend is mixed against.</summary>
    public required string AccentBlendGround { get; init; }

    // ===== Shape =====

    public double RadiusControl { get; init; } = 6;
    public double RadiusCard { get; init; } = 8;
    public double RadiusOverlay { get; init; } = 12;
    public double RadiusFlyout { get; init; } = 16;

    /// <summary>Border thickness for cards and controls. Heavier reads as harder.</summary>
    public double BorderWeight { get; init; } = 1;

    // ===== Type =====

    public string BodyFont { get; init; } = "Segoe UI Variable Text, Segoe UI";
    public string DisplayFont { get; init; } = "Segoe UI Variable Display, Segoe UI";

    // WPF has no letter-spacing on TextBlock, so the industrial feel comes from a
    // condensed display face rather than from tracking.

    // ===== Signal =====

    /// <summary>
    /// The theme's own accent, or null to keep whatever the user or Windows chose.
    /// A designed theme owns its signal colour; the neutral two do not.
    /// </summary>
    public string? SignatureAccent { get; init; }

    /// <summary>Selection, focus and live values are lit from behind.</summary>
    public bool Glows { get; init; }


    /// <summary>
    /// The neutral dark theme. Also the designer and cold-start values in
    /// DesignTokens.xaml, which has to resolve something before any theme is applied.
    /// </summary>
    public static readonly ThemePalette Dark = new()
    {
        IsLight = false,
        SurfaceBase = "#171819",
        SurfaceLayer = "#1D1F20",
        SurfaceCard = "#252728",
        SurfaceCardHover = "#2C2F30",
        SurfaceSunken = "#141516",
        StrokeSubtle = "#2AFFFFFF",
        StrokeDivider = "#18FFFFFF",
        TextPrimary = "#F4F4F4",
        TextSecondary = "#B6B8BA",
        TextTertiary = "#86898C",
        NavigationInk = "#B1B1A9",
        StatusSuccess = "#6CCB5F",
        StatusWarning = "#F2C94C",
        StatusCritical = "#FF6B6B",
        DividerLine = "#26FFFFFF",
        OpaqueSidebar = "#111111",
        OpaqueContent = "#151515",
        MicaContentWash = "#12FFFFFF",
        AccentBlendGround = "#252728",
    };

    public static readonly ThemePalette Light = new()
    {
        IsLight = true,
        SurfaceBase = "#F5F5F5",
        SurfaceLayer = "#FAFAFA",
        SurfaceCard = "#FFFFFFFF",
        SurfaceCardHover = "#FFF3F3F3",
        SurfaceSunken = "#FFEFEFEF",
        StrokeSubtle = "#18000000",
        StrokeDivider = "#12000000",
        TextPrimary = "#1A1A1A",
        TextSecondary = "#5B5B5B",
        TextTertiary = "#777777",
        NavigationInk = "#45453D",
        // Tuned for a dark ground originally; darkened so warning and error text
        // stays readable on white cards.
        StatusSuccess = "#1E7A26",
        StatusWarning = "#9A6700",
        StatusCritical = "#C42B1C",
        DividerLine = "#24000000",
        OpaqueSidebar = "#F3F3F3",
        OpaqueContent = "#FAFAFA",
        MicaContentWash = "#5AFFFFFF",
        AccentBlendGround = "#FFFFFF",
    };

    /// <summary>
    /// The gamer theme.
    /// </summary>
    /// <remarks>
    /// Built from the genre rather than from any one product: black with a blue cast
    /// instead of neutral grey, no corner radius anywhere, borders at 1.5 so a card
    /// reads as a machined panel rather than a soft sheet, and a cyan that only ever
    /// means "this is live or selected". The type is the system UI face, the same as
    /// the other two themes: a display face fighting the shapes read as a skin.
    ///
    /// <para>It carries its own accent. The neutral themes borrow the user's Windows
    /// accent because they are backdrops for whatever colour the desktop is; this one
    /// is a designed object whose signal colour is part of the design, and a pastel
    /// Windows accent would read as a mistake against this ground.</para>
    ///
    /// <para>Contrast was checked against the card colour, not the base: primary ink
    /// #DFF3FA on #11161D is 13.9:1, secondary #93A9B6 is 6.6:1, tertiary #63798A is
    /// 3.6:1 and is only used for text that repeats something already on screen.</para>
    /// </remarks>
    public static readonly ThemePalette Arsenal = new()
    {
        IsLight = false,
        SurfaceBase = "#06080B",
        SurfaceLayer = "#0A0D12",
        SurfaceCard = "#11161D",
        SurfaceCardHover = "#182029",
        SurfaceSunken = "#040608",
        // Visible rather than a hairline: this theme draws its panels rather than
        // letting them float.
        StrokeSubtle = "#3A2C4450",
        StrokeDivider = "#2A2C4450",
        TextPrimary = "#DFF3FA",
        TextSecondary = "#93A9B6",
        TextTertiary = "#63798A",
        NavigationInk = "#9DB6C4",
        StatusSuccess = "#2BE08A",
        StatusWarning = "#FFC53D",
        StatusCritical = "#FF3B4E",
        DividerLine = "#3A00D9FF",
        OpaqueSidebar = "#05070A",
        OpaqueContent = "#080B0F",
        MicaContentWash = "#B0080B0F",
        AccentBlendGround = "#11161D",

        // Radius is left at the shared values on purpose. The hard edge in this theme
        // is drawn, not taken away: the panels, chips, buttons and toggles that carry
        // it are cut corners from AngularBorder, which owns its own outline. Zeroing
        // the radius as well squared every other surface in the app - the Quick Panel
        // card, the info boxes on Home and Battery, every overlay - none of which is
        // part of that idea and all of which simply looked unfinished.
        BorderWeight = 1.5,


        SignatureAccent = "#00D9FF",
        Glows = true,
    };

    /// <summary>
    /// The palette for a stored setting, resolving System against Windows.
    /// </summary>
    public static ThemePalette Resolve(int stored, bool windowsIsLight) => (AppTheme)stored switch
    {
        AppTheme.Arsenal => Arsenal,
        AppTheme.Light => Light,
        AppTheme.Dark => Dark,
        _ => windowsIsLight ? Light : Dark,
    };
}
