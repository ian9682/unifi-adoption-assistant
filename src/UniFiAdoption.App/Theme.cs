using UniFiAdoption.Core.Workflow;

namespace UniFiAdoption.App;

/// <summary>
/// One Acre Fund look for the ITO Networks Team. The palette is built around the logo green
/// (#2B7F68), with a warm harvest yellow used only for the highlight on the drawn label and for
/// keyboard focus.
/// </summary>
internal static class Theme
{
    public const string Organization = "One Acre Fund";
    public const string Team = "ITO Networks Team";
    public const string Motto = "Farmers First!";

    // Brand greens.
    public static readonly Color Brand = Color.FromArgb(0x2B, 0x7F, 0x68);
    public static readonly Color BrandHover = Color.FromArgb(0x25, 0x70, 0x5C);
    public static readonly Color BrandDeep = Color.FromArgb(0x1D, 0x5C, 0x4B);
    public static readonly Color BrandTint = Color.FromArgb(0xEC, 0xF5, 0xF1);
    public static readonly Color BrandLine = Color.FromArgb(0xC6, 0xE0, 0xD7);
    public static readonly Color OnBrand = Color.White;
    public static readonly Color OnBrandSubtle = Color.FromArgb(0xD8, 0xEC, 0xE5);

    // Text and neutrals.
    public static readonly Color Ink = Color.FromArgb(0x1C, 0x2B, 0x27);
    public static readonly Color Muted = Color.FromArgb(0x56, 0x66, 0x61);
    public static readonly Color Disabled = Color.FromArgb(0xB9, 0xCB, 0xC4);
    public static readonly Color Line = Color.FromArgb(0xD8, 0xE2, 0xDE);
    public static readonly Color Marker = Color.FromArgb(0xF2, 0xC1, 0x4E);
    public static readonly Color ErrorText = Color.FromArgb(0x9B, 0x2C, 0x24);

    // Older names kept so other files read naturally.
    public static Color Accent => Brand;
    public static Color AccentPressed => BrandDeep;

    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Small = new("Segoe UI", 9f);
    public static readonly Font Heading = new("Segoe UI Semibold", 11f);
    public static readonly Font TeamName = new("Segoe UI Semibold", 11.5f);
    public static readonly Font MottoFont = new("Segoe UI", 10f, FontStyle.Italic);
    public static readonly Font Title = new("Segoe UI Semibold", 19f);
    public static readonly Font StepNumber = new("Segoe UI Semibold", 10f);
    public static readonly Font ResultTitle = new("Segoe UI Semibold", 13f);
    public static readonly Font Button = new("Segoe UI Semibold", 11.5f);
    public static readonly Font Input = new("Consolas", 24f, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9.5f);

    public static (Color Back, Color Fore) ForTone(ResultTone tone) => tone switch
    {
        ResultTone.Success => (BrandTint, BrandDeep),
        ResultTone.Waiting => (Color.FromArgb(0xFF, 0xF6, 0xDC), Color.FromArgb(0x7A, 0x53, 0x00)),
        ResultTone.Problem => (Color.FromArgb(0xFD, 0xEC, 0xEA), Color.FromArgb(0x9B, 0x2C, 0x24)),
        _ => (Color.FromArgb(0xEE, 0xF2, 0xF4), Color.FromArgb(0x2F, 0x4A, 0x5C)),
    };
}
