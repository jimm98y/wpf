// comctl32's task dialog (TaskDialogIndirect, TaskDialog): the Windows Vista dialog with a main instruction,
// content, command links or buttons, radio buttons, a verification check box, expandable details and a
// footer, built here from WinForms controls laid out as comctl32 v6 lays it out. The Windows API Code
// Pack's TaskDialog (and anything else calling comctl32 directly) came back E_NOTIMPL, which the Code Pack
// turns into a Win32Exception: an application's warning before starting its runtime ended the click.
//
// The application's callback hears what Windows tells it -- TDN_CREATED, TDN_DIALOG_CONSTRUCTED,
// TDN_BUTTON_CLICKED (S_FALSE keeps the dialog open), TDN_RADIO_BUTTON_CLICKED, TDN_VERIFICATION_CLICKED,
// TDN_EXPANDO_BUTTON_CLICKED, TDN_DESTROYED -- with the dialog's window as hwnd. TDM_* messages sent to
// that window (progress bars, changing texts while shown) are not acted on.

using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    // TASKDIALOGCONFIG, packed (comctl32 declares it in pshpack1): the offsets on 64 bits.
    private const int TdcParent = 4, TdcFlags = 20, TdcCommonButtons = 24, TdcTitle = 28, TdcMainIcon = 36,
        TdcMainInstruction = 44, TdcContent = 52, TdcButtonCount = 60, TdcButtons = 64, TdcDefaultButton = 72,
        TdcRadioCount = 76, TdcRadios = 80, TdcDefaultRadio = 88, TdcVerification = 92, TdcExpandedInfo = 100,
        TdcExpandedControl = 108, TdcCollapsedControl = 116, TdcFooterIcon = 124, TdcFooter = 132,
        TdcCallback = 140, TdcCallbackData = 148, TdcWidth = 156;

    private const int TDF_ENABLE_HYPERLINKS = 0x1, TDF_USE_HICON_MAIN = 0x2, TDF_USE_HICON_FOOTER = 0x4,
        TDF_ALLOW_DIALOG_CANCELLATION = 0x8, TDF_USE_COMMAND_LINKS = 0x10, TDF_USE_COMMAND_LINKS_NO_ICON = 0x20,
        TDF_EXPAND_FOOTER_AREA = 0x40, TDF_EXPANDED_BY_DEFAULT = 0x80, TDF_VERIFICATION_FLAG_CHECKED = 0x100,
        TDF_NO_DEFAULT_RADIO_BUTTON = 0x4000, TDF_CAN_BE_MINIMIZED = 0x8000;

    private const uint TDN_CREATED = 0, TDN_BUTTON_CLICKED = 2, TDN_DESTROYED = 5, TDN_RADIO_BUTTON_CLICKED = 6,
        TDN_DIALOG_CONSTRUCTED = 7, TDN_VERIFICATION_CLICKED = 8, TDN_EXPANDO_BUTTON_CLICKED = 10;

    private const int IDOK = 1, IDCANCEL = 2, IDRETRY = 4, IDYES = 6, IDNO = 7, IDCLOSE = 8;

    [Win32Export("TaskDialogIndirect")]
    public static int TaskDialogIndirect(byte* config, int* button, int* radioButton, int* verificationChecked)
    {
        if (config == null) return unchecked((int)0x80070057);   // E_INVALIDARG
        var td = new TaskDialogForm(config);
        nint parent = *(nint*)(config + TdcParent);
        object? owner = parent != 0 ? Resolve(parent) : null;
        Ui(owner, () =>
        {
            Forms.IWin32Window? ownerWindow = owner as Forms.Control;
            td.ShowDialog(ownerWindow);
            return 0;
        });
        if (button != null) *button = td.Result;
        if (radioButton != null) *radioButton = td.RadioResult;
        if (verificationChecked != null) *verificationChecked = td.VerificationChecked ? TRUE : FALSE;
        return 0;   // S_OK
    }

    /// <summary>TaskDialog: the simple form, as TaskDialogIndirect with only these filled in.</summary>
    [Win32Export("TaskDialog")]
    public static int TaskDialog(nint parent, nint instance, char* title, char* mainInstruction, char* content,
                                 int commonButtons, nint icon, int* button)
    {
        byte* config = stackalloc byte[160];
        new Span<byte>(config, 160).Clear();
        *(uint*)config = 160;
        *(nint*)(config + TdcParent) = parent;
        *(int*)(config + TdcCommonButtons) = commonButtons;
        *(char**)(config + TdcTitle) = title;
        *(nint*)(config + TdcMainIcon) = icon;
        *(char**)(config + TdcMainInstruction) = mainInstruction;
        *(char**)(config + TdcContent) = content;
        return TaskDialogIndirect(config, button, null, null);
    }

    /// <summary>The dialog itself, laid out as comctl32 v6 lays a task dialog out.</summary>
    private sealed class TaskDialogForm : Forms.Form
    {
        private readonly delegate* unmanaged<nint, uint, nint, nint, nint, int> _callback;
        private readonly nint _callbackData;
        private readonly int _flags;
        private bool _closing;

        public int Result { get; private set; } = IDCANCEL;
        public int RadioResult { get; private set; }
        public bool VerificationChecked { get; private set; }

        private static string? ReadText(byte* config, int offset)
        {
            nint p = *(nint*)(config + offset);
            // a resource id (MAKEINTRESOURCE) instead of a string: no resource to read it from
            return p == 0 || (ulong)p < 0x10000 ? null : new string((char*)p);
        }

        public TaskDialogForm(byte* config)
        {
            _flags = *(int*)(config + TdcFlags);
            _callback = (delegate* unmanaged<nint, uint, nint, nint, nint, int>)*(nint*)(config + TdcCallback);
            _callbackData = *(nint*)(config + TdcCallbackData);

            string? title = ReadText(config, TdcTitle);
            Text = string.IsNullOrEmpty(title) ? System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "") : title;
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = (_flags & TDF_CAN_BE_MINIMIZED) != 0;
            ShowInTaskbar = false;
            StartPosition = Forms.FormStartPosition.CenterParent;
            AutoScaleMode = Forms.AutoScaleMode.Font;
            Font = new Drawing.Font("Segoe UI", 9f);
            BackColor = Drawing.SystemColors.Control;
            KeyPreview = true;

            // ---- the main area: white, icon to the left of the instruction and content ----
            int width = *(uint*)(config + TdcWidth) is var w && w > 0 ? (int)Math.Round(w * 1.75) : 400;
            var main = new Forms.TableLayoutPanel
            {
                BackColor = Drawing.SystemColors.Window, Dock = Forms.DockStyle.Top, AutoSize = true,
                AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink, ColumnCount = 2, Padding = new Forms.Padding(10, 10, 12, 14),
            };
            main.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
            main.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Absolute, width));

            Drawing.Image? icon = IconImage(*(nint*)(config + TdcMainIcon), (_flags & TDF_USE_HICON_MAIN) != 0, 32);
            var iconBox = new Forms.PictureBox { Image = icon, SizeMode = Forms.PictureBoxSizeMode.AutoSize, Margin = new Forms.Padding(0, 0, 10, 0), Visible = icon != null };
            main.Controls.Add(iconBox, 0, 0);
            main.SetRowSpan(iconBox, 8);

            var body = new Forms.FlowLayoutPanel { FlowDirection = Forms.FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Forms.Padding.Empty };
            main.Controls.Add(body, 1, 0);

            if (ReadText(config, TdcMainInstruction) is { Length: > 0 } instruction)
                body.Controls.Add(Label(instruction, new Drawing.Font("Segoe UI", 12f), Drawing.Color.FromArgb(0x00, 0x33, 0x99), width, new Forms.Padding(0, 0, 0, 8)));
            if (ReadText(config, TdcContent) is { Length: > 0 } content)
                body.Controls.Add(Label(content, Font, Drawing.SystemColors.WindowText, width, new Forms.Padding(0, 0, 0, 8)));

            Forms.Label? expanded = null;
            if (ReadText(config, TdcExpandedInfo) is { Length: > 0 } info && (_flags & TDF_EXPAND_FOOTER_AREA) == 0)
            {
                expanded = Label(info, Font, Drawing.SystemColors.WindowText, width, new Forms.Padding(0, 0, 0, 8));
                expanded.Visible = (_flags & TDF_EXPANDED_BY_DEFAULT) != 0;
                body.Controls.Add(expanded);
            }

            // command links: the buttons in the content area, a line of text each (a second line smaller)
            int buttonCount = (int)*(uint*)(config + TdcButtonCount);
            byte* buttons = *(byte**)(config + TdcButtons);
            bool links = (_flags & (TDF_USE_COMMAND_LINKS | TDF_USE_COMMAND_LINKS_NO_ICON)) != 0;
            int defaultButton = *(int*)(config + TdcDefaultButton);
            Forms.Button? accept = null;
            var custom = new List<(int Id, string Text)>();
            for (int i = 0; buttons != null && i < buttonCount; i++)
            {
                int id = *(int*)(buttons + i * 12);
                nint text = *(nint*)(buttons + i * 12 + 4);
                custom.Add((id, text != 0 && (ulong)text >= 0x10000 ? new string((char*)text) : id.ToString()));
            }
            if (links)
            {
                foreach ((int id, string text) in custom)
                {
                    string[] lines = text.Replace("\r", "").Split('\n', 2);
                    var link = new Forms.Button
                    {
                        Text = lines.Length > 1 ? "→  " + lines[0] + "\n      " + lines[1] : "→  " + lines[0],
                        TextAlign = Drawing.ContentAlignment.MiddleLeft, FlatStyle = Forms.FlatStyle.Flat,
                        ForeColor = Drawing.Color.FromArgb(0x00, 0x33, 0x99), BackColor = Drawing.SystemColors.Window,
                        Font = new Drawing.Font("Segoe UI", 10.5f), AutoSize = true, Width = width, MinimumSize = new Drawing.Size(width, 0),
                        Margin = new Forms.Padding(0, 0, 0, 2),
                    };
                    link.FlatAppearance.BorderSize = 0;
                    link.FlatAppearance.MouseOverBackColor = Drawing.Color.FromArgb(0xE5, 0xF1, 0xFB);
                    link.Click += (_, _) => PressButton(id);
                    body.Controls.Add(link);
                    if (id == defaultButton) accept = link;
                }
                custom.Clear();
            }

            // radio buttons
            int radioCount = (int)*(uint*)(config + TdcRadioCount);
            byte* radios = *(byte**)(config + TdcRadios);
            int defaultRadio = *(int*)(config + TdcDefaultRadio);
            for (int i = 0; radios != null && i < radioCount; i++)
            {
                int id = *(int*)(radios + i * 12);
                nint text = *(nint*)(radios + i * 12 + 4);
                var radio = new Forms.RadioButton
                {
                    Text = text != 0 ? new string((char*)text) : id.ToString(), AutoSize = true, BackColor = Drawing.SystemColors.Window,
                    Checked = (_flags & TDF_NO_DEFAULT_RADIO_BUTTON) == 0 && (id == defaultRadio || (defaultRadio == 0 && i == 0)),
                };
                if (radio.Checked) RadioResult = id;
                radio.CheckedChanged += (_, _) =>
                {
                    if (!radio.Checked) return;
                    RadioResult = id;
                    Notify(TDN_RADIO_BUTTON_CLICKED, id, 0);
                };
                body.Controls.Add(radio);
            }

            // ---- the band below: expando and verification on the left, the buttons on the right ----
            var band = new Forms.TableLayoutPanel
            {
                Dock = Forms.DockStyle.Top, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink,
                ColumnCount = 2, Padding = new Forms.Padding(10, 8, 10, 8), BackColor = Drawing.SystemColors.Control,
            };
            band.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
            band.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
            var left = new Forms.FlowLayoutPanel { FlowDirection = Forms.FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Forms.Padding.Empty };
            band.Controls.Add(left, 0, 0);

            if (expanded != null)
            {
                string more = ReadText(config, TdcCollapsedControl) ?? "See details", less = ReadText(config, TdcExpandedControl) ?? "Hide details";
                var expando = new Forms.CheckBox { Appearance = Forms.Appearance.Normal, AutoSize = true, Text = expanded.Visible ? less : more, Checked = expanded.Visible };
                expando.CheckedChanged += (_, _) =>
                {
                    expanded.Visible = expando.Checked;
                    expando.Text = expando.Checked ? less : more;
                    Notify(TDN_EXPANDO_BUTTON_CLICKED, expando.Checked ? 1 : 0, 0);
                };
                left.Controls.Add(expando);
            }
            if (ReadText(config, TdcVerification) is { Length: > 0 } verification)
            {
                var check = new Forms.CheckBox { Text = verification, AutoSize = true, Checked = (_flags & TDF_VERIFICATION_FLAG_CHECKED) != 0 };
                VerificationChecked = check.Checked;
                check.CheckedChanged += (_, _) =>
                {
                    VerificationChecked = check.Checked;
                    Notify(TDN_VERIFICATION_CLICKED, check.Checked ? 1 : 0, 0);
                };
                left.Controls.Add(check);
            }

            var right = new Forms.FlowLayoutPanel { FlowDirection = Forms.FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = Forms.Padding.Empty, Anchor = Forms.AnchorStyles.Right | Forms.AnchorStyles.Top };
            band.Controls.Add(right, 1, 0);
            int common = *(int*)(config + TdcCommonButtons);
            var all = new List<(int Id, string Text)>(custom);
            foreach ((int bit, int id, string text) in new[] { (0x1, IDOK, "OK"), (0x2, IDYES, "&Yes"), (0x4, IDNO, "&No"), (0x10, IDRETRY, "&Retry"), (0x8, IDCANCEL, "Cancel"), (0x20, IDCLOSE, "&Close") })
                if ((common & bit) != 0) all.Add((id, text));
            if (all.Count == 0 && !links) all.Add((IDOK, "OK"));   // Windows shows OK when no button is asked for
            Forms.Button? cancel = null;
            foreach ((int id, string text) in all)
            {
                var b = new Forms.Button { Text = text, AutoSize = true, MinimumSize = new Drawing.Size(75, 23), Margin = new Forms.Padding(6, 0, 0, 0) };
                b.Click += (_, _) => PressButton(id);
                right.Controls.Add(b);
                if (id == defaultButton || (defaultButton == 0 && accept == null)) accept ??= b;
                if (id == IDCANCEL) cancel = b;
            }
            AcceptButton = accept;
            // Esc and the close box cancel when the dialog can be cancelled, as on Windows
            bool cancellable = cancel != null || (_flags & TDF_ALLOW_DIALOG_CANCELLATION) != 0;
            ControlBox = cancellable || all.Count == 1;
            if (cancel != null) CancelButton = cancel;

            // footer
            Forms.Control? footer = null;
            if (ReadText(config, TdcFooter) is { Length: > 0 } footerText)
            {
                var row = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Top, AutoSize = true, Padding = new Forms.Padding(10, 6, 10, 8), BackColor = Drawing.SystemColors.Control };
                Drawing.Image? footerIcon = IconImage(*(nint*)(config + TdcFooterIcon), (_flags & TDF_USE_HICON_FOOTER) != 0, 16);
                if (footerIcon != null) row.Controls.Add(new Forms.PictureBox { Image = footerIcon, SizeMode = Forms.PictureBoxSizeMode.AutoSize });
                row.Controls.Add(Label(footerText, Font, Drawing.SystemColors.ControlText, width, Forms.Padding.Empty));
                footer = row;
            }

            // one column: the main area, the band, the footer -- measured, and the dialog sized to it
            var root = new Forms.TableLayoutPanel { ColumnCount = 1, AutoSize = true, Margin = Forms.Padding.Empty, Padding = Forms.Padding.Empty, BackColor = Drawing.SystemColors.Control };
            main.Dock = Forms.DockStyle.Fill;
            band.Dock = Forms.DockStyle.Fill;
            root.Controls.Add(main, 0, 0);
            root.Controls.Add(band, 0, 1);
            if (footer != null)
            {
                footer.Dock = Forms.DockStyle.Fill;
                root.Controls.Add(footer, 0, 2);
            }
            Controls.Add(root);
            Drawing.Size preferred = root.GetPreferredSize(Drawing.Size.Empty);
            ClientSize = preferred;
            root.Dock = Forms.DockStyle.Fill;
        }

        private static Forms.Label Label(string text, Drawing.Font font, Drawing.Color color, int width, Forms.Padding margin)
            => new Forms.Label
            {
                Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), Font = font, ForeColor = color,
                AutoSize = true, MaximumSize = new Drawing.Size(width, 0), Margin = margin, UseMnemonic = false,
            };

        private static Drawing.Image? IconImage(nint icon, bool isHandle, int size)
        {
            Drawing.Icon? i = null;
            if (isHandle && icon != 0)
            {
                try { i = Drawing.Icon.FromHandle(icon); } catch (Exception) { }
            }
            else
            {
                i = (int)icon switch
                {
                    65535 => Drawing.SystemIcons.Warning,       // TD_WARNING_ICON
                    65534 => Drawing.SystemIcons.Error,         // TD_ERROR_ICON
                    65533 => Drawing.SystemIcons.Information,   // TD_INFORMATION_ICON
                    65532 => Drawing.SystemIcons.Shield,        // TD_SHIELD_ICON
                    32512 => Drawing.SystemIcons.Application,
                    32513 => Drawing.SystemIcons.Error,
                    32514 => Drawing.SystemIcons.Question,
                    32515 => Drawing.SystemIcons.Warning,
                    32516 => Drawing.SystemIcons.Information,
                    _ => null,
                };
            }
            if (i == null) return null;
            try { return new Drawing.Icon(i, size, size).ToBitmap(); }
            catch (Exception) { return i.ToBitmap(); }
        }

        private int Notify(uint notification, nint wParam, nint lParam)
        {
            if (_callback == null || !IsHandleCreated) return 0;
            try { return _callback(Handle, notification, wParam, lParam, _callbackData); }
            catch (Exception) { return 0; }
        }

        /// <summary>A button: TDN_BUTTON_CLICKED first, which may keep the dialog open (S_FALSE).</summary>
        private void PressButton(int id)
        {
            if (Notify(TDN_BUTTON_CLICKED, id, 0) == 1) return;   // S_FALSE
            Result = id;
            _closing = true;
            Close();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Notify(TDN_DIALOG_CONSTRUCTED, 0, 0);
            Notify(TDN_CREATED, 0, 0);
        }

        protected override void OnFormClosing(Forms.FormClosingEventArgs e)
        {
            // the close box or Esc: IDCANCEL, which the callback hears as a button too
            if (!_closing)
            {
                if (Notify(TDN_BUTTON_CLICKED, IDCANCEL, 0) == 1) { e.Cancel = true; return; }
                Result = IDCANCEL;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(Forms.FormClosedEventArgs e)
        {
            Notify(TDN_DESTROYED, 0, 0);
            base.OnFormClosed(e);
        }
    }
}
