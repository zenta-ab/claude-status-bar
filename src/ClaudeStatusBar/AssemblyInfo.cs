using System.Runtime.CompilerServices;

// Lets tests exercise pure internal logic directly (e.g. Icons/IconSlot.cs's tooltip
// composition) without constructing the GDI/NotifyIcon objects that own it, mirroring how
// Ui/PanelAnchor.cs already marks its own pure geometry `internal`.
[assembly: InternalsVisibleTo("ClaudeStatusBar.Tests")]
