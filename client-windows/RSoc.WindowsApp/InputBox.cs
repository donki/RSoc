namespace RSoc.WindowsApp;

/// <summary>Diálogo modal para introducir una contraseña (ofuscada, con opción de mostrarla).</summary>
internal static class InputBox
{
    public static string? Show(string title, string prompt, string defaultValue = "")
    {
        using var form = new Form
        {
            Text = title,
            Width = 380,
            Height = 190,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
        };

        var label = new Label { Left = 12, Top = 12, Width = 344, Text = prompt };
        var input = new TextBox { Left = 12, Top = 38, Width = 344, Text = defaultValue, UseSystemPasswordChar = true };
        var show = new CheckBox { Left = 12, Top = 68, Width = 200, Text = "Ver contraseña" };
        show.CheckedChanged += (_, _) => input.UseSystemPasswordChar = !show.Checked;

        var ok = new Button { Text = "Aceptar", Left = 196, Top = 104, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", Left = 281, Top = 104, Width = 75, DialogResult = DialogResult.Cancel };

        form.Controls.Add(label);
        form.Controls.Add(input);
        form.Controls.Add(show);
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK ? input.Text : null;
    }
}
