// Resolves the type-name collisions that arise from referencing both WPF (System.Windows.*) and
// WinForms (System.Windows.Forms, used only for the NotifyIcon tray icon) in the same project.
// WPF wins every ambiguous name project-wide; TrayIconFactory/TrayController opt back into the
// WinForms types they actually need via local `using` aliases where required.
global using Application = System.Windows.Application;
global using HorizontalAlignment = System.Windows.HorizontalAlignment;
global using CheckBox = System.Windows.Controls.CheckBox;
global using Orientation = System.Windows.Controls.Orientation;
