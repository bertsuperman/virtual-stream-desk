using System;
using System.Drawing;
using System.Windows.Forms;
using VirtualStreamDeck.Core;

namespace VirtualStreamDeck.App.Services;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Action _languageChanged;

    public TrayIcon(string text, Action toggleWindow, Action openSettings, Action exit)
    {
        var menu = new ContextMenuStrip();
        var show = menu.Items.Add("", null, (_, _) => toggleWindow());
        var settings = menu.Items.Add("", null, (_, _) => openSettings());
        menu.Items.Add(new ToolStripSeparator());
        var quit = menu.Items.Add("", null, (_, _) => exit());
        void Texts()
        {
            show.Text = L.T("Zobraziť / skryť", "Show / hide");
            settings.Text = L.T("Nastavenia…", "Settings…");
            quit.Text = L.T("Ukončiť", "Quit");
        }
        Texts();
        _languageChanged = Texts;
        L.Changed += _languageChanged;

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
        L.Changed -= _languageChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
