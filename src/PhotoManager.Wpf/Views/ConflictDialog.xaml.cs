using System.Windows;
using PhotoManager.Application.Transfer;

namespace PhotoManager.Wpf.Views;

public partial class ConflictDialog : Window
{
    private ConflictPolicy? _choice;

    public ConflictDialog() { InitializeComponent(); }

    public static ConflictPolicy? Ask(Window? owner, int count, string example, bool move)
    {
        var dialog = new ConflictDialog();
        if (owner is not null) dialog.Owner = owner;
        dialog.Heading.Text = count == 1 ? $"“{example}” já existe no destino." : $"{count} itens já existem no destino (ex.: “{example}”).";
        dialog.Detail.Text = $"Como {(move ? "mover" : "copiar")} os itens com o mesmo nome?";
        dialog.Loaded += (_, _) => dialog.KeepBothButton.Focus();          // a opção mais segura é a padrão do teclado
        return dialog.ShowDialog() == true ? dialog._choice : null;
    }

    private void Choose(ConflictPolicy policy) { _choice = policy; DialogResult = true; }
    private void KeepBoth_Click(object sender, RoutedEventArgs e) => Choose(ConflictPolicy.KeepBoth);
    private void Skip_Click(object sender, RoutedEventArgs e) => Choose(ConflictPolicy.Skip);
    private void Replace_Click(object sender, RoutedEventArgs e) => Choose(ConflictPolicy.Replace);
}
