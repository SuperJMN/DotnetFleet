using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DotnetFleet.Dialogs;

public partial class ConnectDialogView : UserControl
{
    public ConnectDialogView() => AvaloniaXamlLoader.Load(this);
}
