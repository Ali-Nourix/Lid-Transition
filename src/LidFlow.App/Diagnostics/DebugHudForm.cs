using System;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using LidFlow.Core.Monitors;

namespace LidFlow.App.Diagnostics;

/// <summary>
/// Developer overlay showing live pipeline state.
/// <para>
/// A separate small window rather than text drawn inside the transition itself,
/// for two reasons: the transition must contain no text or chrome of any kind,
/// and a HUD drawn into the overlay would be captured into the next snapshot.
/// It is only created when <c>Diagnostics.ShowDebugHud</c> is set, which is off
/// in the shipped default configuration.
/// </para>
/// </summary>
internal sealed class DebugHudForm : Form
{
    private readonly Func<DiagnosticsSnapshot> _source;
    private readonly Label _text = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    public DebugHudForm(Func<DiagnosticsSnapshot> source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));

        Text = "LidFlow diagnostics";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(400, 330);
        BackColor = Color.FromArgb(18, 18, 20);

        _text.Dock = DockStyle.Fill;
        _text.ForeColor = Color.FromArgb(225, 230, 240);
        _text.Font = AppIcon.FirstAvailableFont(8.5f, FontStyle.Regular, "Cascadia Mono", "Consolas", "Courier New");
        _text.Padding = new Padding(10);
        _text.UseMnemonic = false;
        Controls.Add(_text);

        Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(work.Right - Width - 24, work.Top + 24);

        _timer.Interval = 250;
        _timer.Tick += (_, _) => Refresh(_source());
        _timer.Start();
    }

    private void Refresh(DiagnosticsSnapshot snapshot)
    {
        StringBuilder builder = new(512);
        CultureInfo culture = CultureInfo.InvariantCulture;

        builder.AppendLine(culture, $"state          {snapshot.State}");
        builder.AppendLine(culture, $"panel progress {snapshot.PanelProgress:0.000}");
        builder.AppendLine(culture, $"fps            {snapshot.Fps:0.0}");
        builder.AppendLine(culture, $"gpu frame      {snapshot.LastFrameMs:0.00} ms");
        builder.AppendLine();
        builder.AppendLine(culture, $"capture        {snapshot.CaptureBackend}");
        builder.AppendLine(culture, $"capture time   {snapshot.CaptureLatencyMs:0.0} ms");
        builder.AppendLine(culture, $"overlay hidden {(snapshot.CaptureExcluded ? "from capture" : "NOT excluded")}");
        builder.AppendLine();
        builder.AppendLine(culture, $"adapter        {snapshot.Adapter}{(snapshot.SoftwareRenderer ? " (WARP)" : string.Empty)}");
        builder.AppendLine(culture, $"display power  {snapshot.DisplayPower}");
        builder.AppendLine(culture, $"input          {DescribeSource(snapshot.AngleSource)}");

        if (snapshot.HingeAngleDegrees is double angle)
        {
            builder.AppendLine(culture, $"hinge angle    {angle:0.0} deg");
        }

        if (snapshot.LidPitchDegrees is double pitch)
        {
            builder.AppendLine(culture, $"lid pitch      {pitch:0.0} deg ({snapshot.PitchSensor})");
        }

        if (snapshot.CalibrationSweepDegrees is double sweep)
        {
            builder.AppendLine(culture, $"calibration    {sweep:0.#} deg sweep");
        }
        else
        {
            builder.AppendLine("calibration    not yet learned");
        }
        builder.AppendLine();

        DisplayInfo? display = snapshot.Display;

        if (display is null)
        {
            builder.AppendLine("display        <none selected>");
        }
        else
        {
            builder.AppendLine(culture, $"display        {display.FriendlyName}");
            builder.AppendLine(culture, $"  id           {display.Id}");
            builder.AppendLine(culture, $"  bounds       {display.Bounds}");
            builder.AppendLine(culture, $"  dpi          {display.DpiScale * 100f:0}%  {display.RefreshHz} Hz");
            builder.AppendLine(culture, $"  internal     {display.InternalConfidence}");
            builder.AppendLine(culture, $"  hdr          {display.IsHdr}");
        }

        builder.Append(culture, $"selection      {snapshot.SelectionReason}");

        _text.Text = builder.ToString();
    }

    private static string DescribeSource(LidFlow.Core.Lid.LidAngleSourceKind source) => source switch
    {
        LidFlow.Core.Lid.LidAngleSourceKind.HingeAngleSensor => "hinge angle sensor (exact)",
        LidFlow.Core.Lid.LidAngleSourceKind.LidInclinometer => "lid inclinometer (tracked)",
        _ => "timed animation",
    };

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
