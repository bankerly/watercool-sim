using System;
using System.Drawing;
using System.Windows.Forms;

public class TrayProbe
{
    [STAThread]
    public static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Form f = new Form();
        f.Text = "WCS-TrayProbe";
        f.Width = 300;
        f.Height = 120;
        NotifyIcon ni = new NotifyIcon();
        ni.Icon = SystemIcons.Application;
        ni.Text = "WCS-TRAYPROBE";
        ni.Visible = true;
        ni.BalloonTipTitle = "probe";
        ni.BalloonTipText = "probe icon";
        ni.ShowBalloonTip(3000);
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 6000;
        t.Tick += delegate {
            try {
                string p = args.Length > 0 ? args[0] : "trayprobe.log";
                System.IO.File.AppendAllText(p, "visible=" + ni.Visible + " icon=" + (ni.Icon != null) + Environment.NewLine);
            } catch {}
            Application.Exit();
        };
        t.Start();
        Application.Run(f);
    }
}
