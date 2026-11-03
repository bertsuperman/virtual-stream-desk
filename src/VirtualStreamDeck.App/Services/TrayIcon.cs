using System;
using System.Drawing;
using System.Windows.Forms;

namespace VirtualStreamDeck.App.Services;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(string text, Action toggleWindow, Action openSettings, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Zobraziť / skryť", null, (_, _) => toggleWindow());
        menu.Items.Add("Nastavenia…", null, (_, _) => openSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Ukončiť", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Text = text.Length > 63 ? text[..63] : text, // NotifyIcon limit
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) toggleWindow();
        };
    }

    private static Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
                return Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application;
        }
        catch
        {
            // fall through
        }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
