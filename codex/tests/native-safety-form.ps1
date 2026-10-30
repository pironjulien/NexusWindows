# Local acceptance fixture. Its Envoyer button only changes this form's title and emits a local event.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Windows.Forms;
public class NativeSafetyForm : Form {
    protected override bool ShowWithoutActivation { get { return true; } }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    int sent;
    public NativeSafetyForm() {
        Text = "Native safety fixture"; StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); ClientSize = new Size(420, 170);
        Controls.Add(new TextBox { Name = "nom", AccessibleName = "Nom", Location = new Point(10, 15), Width = 390 });
        var password = new TextBox { Name = "secret", AccessibleName = "Mot de passe", UseSystemPasswordChar = true, Location = new Point(10, 100), Width = 390 };
        password.TextChanged += (s,e) => { Console.WriteLine("{\"event\":\"password changed\"}"); }; Controls.Add(password);
        var send = new Button { Text = "Envoyer", Location = new Point(10, 55), Width = 120 };
        send.Click += (s,e) => { Text = "Native safety clicked " + (++sent); Console.WriteLine("{\"event\":\"local send " + sent + "\"}"); }; Controls.Add(send);
        var close = new Button { Text = "Fermer avec délai", Location = new Point(150, 55), Width = 160 };
        close.Click += (s,e) => { var timer = new Timer { Interval = 200 }; timer.Tick += (a,b) => { timer.Stop(); Console.WriteLine("{\"event\":\"delayed close\"}"); Close(); timer.Dispose(); }; timer.Start(); }; Controls.Add(close);
        var toggle = new Button { Text = "Toggle readonly", Location = new Point(10, 135), Width = 160 };
        toggle.Click += (s,e) => { var field = (TextBox)Controls["nom"]; field.ReadOnly = !field.ReadOnly; Text = "Native safety readonly " + (field.ReadOnly ? "on" : "off"); }; Controls.Add(toggle);
        Shown += (s,e) => { SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x13); Location = new Point(160, 160); Console.WriteLine("{\"ready\":true,\"top\":" + Handle.ToInt64() + "}"); };
    }
}
'@
[System.Windows.Forms.Application]::Run((New-Object NativeSafetyForm))
