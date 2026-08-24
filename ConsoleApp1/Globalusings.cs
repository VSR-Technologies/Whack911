// SIPSorceryMedia.Windows transitively pulls in a reference that exposes
// System.Windows.Forms types alongside WPF's System.Windows types, causing
// ambiguous reference errors for names that exist in both namespaces
// (Window, MessageBox, KeyEventArgs, etc). These global aliases pin each
// name to the WPF version project-wide, so individual files don't need to
// fully-qualify every occurrence.
global using Window = System.Windows.Window;
global using MessageBox = System.Windows.MessageBox;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using Application = System.Windows.Application;