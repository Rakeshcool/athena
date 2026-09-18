// Resolve the WinForms/WPF ambiguities once, project-wide. The tray icon drags
// WinForms in (NotifyIcon has no WPF equivalent) and its implicit usings shadow
// half of WPF's type names.

global using MessageBox = System.Windows.MessageBox;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using Rectangle = System.Windows.Shapes.Rectangle;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using Orientation = System.Windows.Controls.Orientation;
global using HorizontalAlignment = System.Windows.HorizontalAlignment;
global using VerticalAlignment = System.Windows.VerticalAlignment;
