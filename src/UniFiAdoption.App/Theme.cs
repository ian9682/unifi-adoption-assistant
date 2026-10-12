using UniFiAdoption.Core.Workflow;

namespace UniFiAdoption.App;

/// <summary>
/// One calm palette. The single bold element is the yellow marker that highlights the four
/// characters on the drawn label, echoing what the person types.
/// </summary>
internal static class Theme
{
    public static readonly Color Ink = Color.FromArgb(0x1F, 0x2A, 0x33);
    public static readonly Color Muted = Color.FromArgb(0x56, 0x63, 0x6D);
    public static readonly Color Accent = Color.FromArgb(0x0E, 0x5F, 0x73);
    public static readonly Color AccentPressed = Color.FromArgb(0x0A, 0x4A, 0x5A);
    public static readonly Color Line = Color.FromArgb(0xD5, 0xDC, 0xE0);
    public static readonly Color Marker = Color.FromArgb(0xFF, 0xD8, 0x4A);
    public static readonly Color ErrorText = Color.FromArgb(0x9B, 0x2C, 0x24);

    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Small = new("Segoe UI", 9f);
    public static readonly Font Heading = new("Segoe UI Semibold", 11f);
    public static readonly Font Title = new("Segoe UI Semibold", 17f);
    public static readonly Font ResultTitle = new("Segoe UI Semibold", 13f);
    public static readonly Font Button = new("Segoe UI Semibold", 11.5f);
    public static readonly Font Input = new("Consolas", 24f, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9.5f);

    public static (Color Back, Color Fore) ForTone(ResultTone tone) => tone switch
    {
        ResultTone.Success => (Color.FromArgb(0xE6, 0xF4, 0xEA), Color.FromArgb(0x1D, 0x6B, 0x3A)),
        ResultTone.Waiting => (Color.FromArgb(0xFF, 0xF4, 0xD6), Color.FromArgb(0x7A, 0x53, 0x00)),
        ResultTone.Problem => (Color.FromArgb(0xFD, 0xEC, 0xEA), Color.FromArgb(0x9B, 0x2C, 0x24)),
        _ => (Color.FromArgb(0xE8, 0xF0, 0xF7), Color.FromArgb(0x1E, 0x4E, 0x78)),
    };
}
