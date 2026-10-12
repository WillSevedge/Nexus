using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;

namespace Nexus.Hub.Excel;

/// <summary>
/// A linked workbook that is open in Excel: Excel locks the file, so Nexus writes into the open workbook through
/// Excel itself (found in Windows' running object table, like Excel's GetObject). The values appear straight away;
/// the user saves in Excel. Excel calculates formulas and extends tables itself.
/// </summary>
internal static class ExcelLive
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ctx);

    /// <summary>The workbook object when <paramref name="path"/> is open in a running Excel; otherwise null.</summary>
    public static object? FindOpenWorkbook(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return null; }
        IRunningObjectTable? rot = null;
        IEnumMoniker? monikers = null;
        try
        {
            if (GetRunningObjectTable(0, out rot) != 0 || CreateBindCtx(0, out var ctx) != 0) return null;
            rot.EnumRunning(out monikers);
            var one = new IMoniker[1];
            while (monikers.Next(1, one, IntPtr.Zero) == 0)
            {
                try
                {
                    one[0].GetDisplayName(ctx, null, out string name);
                    if (string.IsNullOrEmpty(name) || !string.Equals(name, full, StringComparison.OrdinalIgnoreCase)) continue;
                    rot.GetObject(one[0], out object workbook);
                    return workbook;
                }
                catch (COMException) { /* not ours */ }
                finally
                {
                    Marshal.ReleaseComObject(one[0]);
                }
            }
        }
        catch (Exception ex)
        {
            HubLog.Warn("Looking for the workbook in Excel failed.", ex);
        }
        finally
        {
            if (monikers is not null) Marshal.ReleaseComObject(monikers);
            if (rot is not null) Marshal.ReleaseComObject(rot);
        }
        return null;
    }

    /// <summary>
    /// Applies a plan to the open workbook: makes the table longer first (Excel copies formulas and formats),
    /// then sets the cells. Retries while Excel is busy (a cell being edited, a dialog open).
    /// </summary>
    public static void Apply(object workbook, ExcelFillPlan plan)
    {
        dynamic wb = workbook;
        dynamic app = wb.Application;
        dynamic ws = Retry(() => wb.Worksheets[plan.Worksheet]);
        bool screen = true;
        try
        {
            screen = Retry(() => (bool)app.ScreenUpdating);
            Retry(() => { app.ScreenUpdating = false; return 0; });
            if (plan.GrowToRow > 0 && plan.Table is { } tableName)
            {
                Retry(() =>
                {
                    dynamic table = ws.ListObjects[tableName];
                    dynamic range = table.Range;
                    int top = (int)range.Row, left = (int)range.Column, width = (int)range.Columns.Count;
                    table.Resize(ws.Range[ws.Cells[top, left], ws.Cells[plan.GrowToRow, left + width - 1]]);
                    return 0;
                });
            }
            foreach (var u in plan.Updates)
            {
                object value = ToExcel(u.Value);
                Retry(() => { ws.Cells[u.Row, u.Column].Value2 = value; return 0; });
            }
        }
        finally
        {
            try { app.ScreenUpdating = screen; } catch { /* Excel closed meanwhile */ }
        }
    }

    /// <summary>Numbers as numbers, dates as dates, everything else as text exactly as given (no conversion by Excel).</summary>
    private static object ToExcel(string text)
    {
        if (text.Length == 0) return "";
        bool leadingZero = text.Length > 1 && text[0] == '0' && char.IsDigit(text[1]);
        if (!leadingZero && double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double number)
            && !text.Contains(',', StringComparison.Ordinal))
            return number;
        if (text.Any(char.IsDigit) && (text.Contains('/') || text.Contains('-') && text.Length <= 10)
            && DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date)
            && !text.Any(char.IsLetter))
            return date.ToOADate();
        // Text that Excel would turn into a number or date on its own ("001", "1-2") stays text.
        bool looksConvertible = double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out _)
                                || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out _);
        return looksConvertible ? "'" + text : text;
    }

    private const int CallRejected = unchecked((int)0x80010001);
    private const int Busy = unchecked((int)0x8001010A);

    private static T Retry<T>(Func<T> call)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return call(); }
            catch (COMException ex) when ((ex.HResult == CallRejected || ex.HResult == Busy) && attempt < 40)
            {
                Thread.Sleep(250);
            }
            catch (COMException ex) when (ex.HResult == CallRejected || ex.HResult == Busy)
            {
                throw new InvalidOperationException("Excel is busy (a cell is being edited or a dialog is open). Press Enter or Esc in Excel and try again.", ex);
            }
        }
    }
}
